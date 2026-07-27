// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Migration.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Barbatos.Migration.EntityFrameworkCore.UnitTests;

public sealed class EfCoreProviderTests : IDisposable
{
    private readonly string _root;

    public EfCoreProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "barbatos-migration-ef-tests", Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(_root, "Data");
        Directory.CreateDirectory(DataDirectory);
    }

    private string DataDirectory { get; }

    private string BackupRoot => Path.Combine(_root, ".migration");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TestDbContext CreateContext(IMigrationContext context) =>
        new(new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite($"Data Source={context.GetWorkingPath("app.db")}")
            .Options);

    private MigrationOptions Options(Action<MigrationOptions>? configure = null)
    {
        MigrationOptions options = new()
        {
            DataDirectory = DataDirectory,
            BackupRootDirectory = BackupRoot,
            TargetDataVersion = new Version(2, 0, 0),
            InitialDataVersion = new Version(1, 0, 0),
            SkipFreeSpaceCheck = true,
        };

        configure?.Invoke(options);
        return options;
    }

    private void Stamp(Version version) => new FileDataVersionStore(DataDirectory).Write(version, []);

    private List<string> ReadColumns()
    {
        using TestDbContext context = new(new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite($"Data Source={Path.Combine(DataDirectory, "app.db")}")
            .Options);

        using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Users');";
        context.Database.OpenConnection();

        List<string> columns = [];
        using System.Data.Common.DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(0));

        context.Database.CloseConnection();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return columns;
    }

    [Fact]
    public async Task EF_Core_migrations_are_applied_inside_the_engines_run()
    {
        Stamp(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            Options(),
            [new MigrationStep(new Version(2, 0, 0), "Apply the EF schema",
                new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite })]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        ReadColumns().Should().Contain(["Id", "FullName", "FirstName", "LastName"]);
    }

    [Fact]
    public async Task A_second_run_reports_nothing_pending()
    {
        Stamp(new Version(1, 0, 0));

        List<string> log = [];
        MigrationOptions options = Options(o => o.Logger = new DelegateMigrationLogger((level, message, _) => log.Add($"{level}: {message}")));

        MigrationStep step = new(new Version(2, 0, 0), "Apply the EF schema",
            new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite });

        await new MigrationEngine(options, [step]).RunAsync();

        // A fresh engine at a higher target, so the same provider runs again against an
        // already-migrated database.
        MigrationResult second = await new MigrationEngine(
            Options(o =>
            {
                o.TargetDataVersion = new Version(3, 0, 0);
                o.Logger = options.Logger;
            }),
            [step, new MigrationStep(new Version(3, 0, 0), "Apply again",
                new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite })]).RunAsync();

        second.Outcome.Should().Be(MigrationOutcome.Succeeded);
        log.Should().Contain(entry => entry.Contains("no pending EF Core migrations"));
    }

    [Fact]
    public async Task The_database_file_is_released_so_a_rollback_is_not_blocked_by_it()
    {
        Stamp(new Version(1, 0, 0));
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), """{ "theme": "dark" }""");

        // The provider succeeds, then a later provider in the same step throws. Rolling back
        // renames the directory the .db sits in, which Windows refuses while any handle is open
        // - and disposing a DbContext returns its connection to the pool rather than closing the
        // file. This is the scenario the Dialect property exists for.
        MigrationResult result = await new MigrationEngine(
            Options(),
            [new MigrationStep(
                new Version(2, 0, 0),
                "EF migration then failure",
                new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite },
                new ThrowingProvider())]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed, "the rollback must not be blocked by an open database handle");
        result.RollbackError.Should().BeNull();

        File.Exists(Path.Combine(DataDirectory, "app.db")).Should().BeFalse("the database did not exist before the run");
        File.ReadAllText(Path.Combine(DataDirectory, "settings.json")).Should().Be("""{ "theme": "dark" }""");
    }

    [Fact]
    public async Task DbContextMigrationProvider_transforms_data_through_the_model()
    {
        Stamp(new Version(1, 0, 0));

        // Schema first, then the data transformation - two providers, one step, applied together.
        MigrationResult result = await new MigrationEngine(
            Options(),
            [new MigrationStep(
                new Version(2, 0, 0),
                "Split the name",
                new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite },
                new SeedProvider(),
                new DbContextMigrationProvider<TestDbContext>(
                    "Split Users.FullName",
                    CreateContext,
                    up: async (db, progress, ct) =>
                    {
                        List<User> users = await db.Users.Where(user => user.FirstName == null).ToListAsync(ct);

                        for (int i = 0; i < users.Count; i++)
                        {
                            string[] parts = users[i].FullName.Split(' ', 2);
                            users[i].FirstName = parts[0];
                            users[i].LastName = parts.Length > 1 ? parts[1] : string.Empty;
                            progress?.Report(new MigrationProgress(i * 100.0 / users.Count, $"User {i + 1}/{users.Count}"));
                        }

                        await db.SaveChangesAsync(ct);
                    })
                {
                    Dialect = DatabaseDialects.Sqlite,
                })]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        using TestDbContext context = new(new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite($"Data Source={Path.Combine(DataDirectory, "app.db")}")
            .Options);

        List<User> migrated = await context.Users.OrderBy(user => user.Id).ToListAsync();
        migrated.Select(user => user.FirstName).Should().Equal(["Pham", "Ada"]);
        migrated.Select(user => user.LastName).Should().Equal(["The Hung", "Lovelace"]);
    }

    [Fact]
    public async Task A_failing_data_transformation_rolls_the_whole_run_back()
    {
        Stamp(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            Options(),
            [
                new MigrationStep(new Version(1, 5, 0), "Schema and seed",
                    new EfCoreMigrationsProvider<TestDbContext>(CreateContext) { Dialect = DatabaseDialects.Sqlite },
                    new SeedProvider()),
                new MigrationStep(new Version(2, 0, 0), "Broken transformation",
                    new DbContextMigrationProvider<TestDbContext>(
                        "Explodes",
                        CreateContext,
                        up: (_, _, _) => throw new InvalidOperationException("nope"))
                    {
                        Dialect = DatabaseDialects.Sqlite,
                    }),
            ]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        result.RollbackError.Should().BeNull("the EF connection was released before the rollback ran");

        // The 1.5.0 step is undone too: every step in a run shares one snapshot.
        File.Exists(Path.Combine(DataDirectory, "app.db")).Should().BeFalse();
        new FileDataVersionStore(DataDirectory).Read().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public void EfCoreMigrationsProvider_is_forward_only_and_says_what_to_use_instead()
    {
        EfCoreMigrationsProvider<TestDbContext> provider = new(CreateContext);

        provider.CanDown.Should().BeFalse();
        provider.Name.Should().Be("EF Core (TestDbContext)");
        provider.Weight.Should().Be(5.0);

        Func<Task> act = () => provider.DownAsync(null!, null, CancellationToken.None);

        act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*EfCoreDowngradeMigrationsProvider*");
    }

    [Fact]
    public async Task EfCoreDowngradeMigrationsProvider_migrates_to_a_named_target_both_ways()
    {
        Stamp(new Version(1, 0, 0));

        EfCoreDowngradeMigrationsProvider<TestDbContext> provider = new(
            CreateContext,
            upTargetMigration: "20260201000000_SplitName",
            downTargetMigration: "20260101000000_CreateUsers")
        {
            Dialect = DatabaseDialects.Sqlite,
        };

        provider.CanDown.Should().BeTrue();

        MigrationStep step = new(new Version(2, 0, 0), "Schema", provider);

        await new MigrationEngine(Options(), [step]).RunAsync();
        ReadColumns().Should().Contain("FirstName");

        MigrationResult down = await new MigrationEngine(
            Options(options =>
            {
                options.TargetDataVersion = new Version(1, 0, 0);
                options.AllowDowngrade = true;
            }),
            [step]).RunAsync();

        down.Outcome.Should().Be(MigrationOutcome.Succeeded);
        ReadColumns().Should().Contain("FullName").And.NotContain("FirstName");
    }

    [Fact]
    public void The_providers_reject_a_null_context_factory()
    {
        ((Action)(() => new EfCoreMigrationsProvider<TestDbContext>(null!)))
            .Should().Throw<ArgumentNullException>();

        ((Action)(() => new DbContextMigrationProvider<TestDbContext>("x", null!, (_, _, _) => Task.CompletedTask)))
            .Should().Throw<ArgumentNullException>();
    }

    /// <summary>Puts two rows in, so a data transformation has something to transform.</summary>
    private sealed class SeedProvider : MigrationProvider
    {
        public override string Name => "Seed";

        public override async Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            await using TestDbContext db = CreateContext(context);

            db.Users.AddRange(
                new User { FullName = "Pham The Hung" },
                new User { FullName = "Ada Lovelace" });

            await db.SaveChangesAsync(cancellationToken);
            await db.DisposeAsync();

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private sealed class ThrowingProvider : MigrationProvider
    {
        public override string Name => "Explodes";

        public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("after the schema change");
    }
}
