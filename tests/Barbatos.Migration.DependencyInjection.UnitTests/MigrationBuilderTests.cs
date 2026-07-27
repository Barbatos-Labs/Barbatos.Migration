// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Migration.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Barbatos.Migration.DependencyInjection.UnitTests;

/// <summary>
/// Every method on <see cref="MigrationBuilder"/>, each shown doing the thing it exists for.
/// </summary>
public class MigrationBuilderTests
{
    private static ServiceCollection Services(TempDirectory temp, Action<MigrationOptions>? extra = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddBarbatosMigration(options =>
        {
            options.DataDirectory = temp.Data;
            options.BackupRootDirectory = temp.BackupRoot;
            options.TargetDataVersion = new Version(2, 0, 0);
            options.InitialDataVersion = new Version(1, 0, 0);
            options.SkipFreeSpaceCheck = true;
            extra?.Invoke(options);
        });

        return services;
    }

    [Fact]
    public async Task AddStep_with_an_instance_registers_it()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep(new MigrationStep(new Version(2, 0, 0), "Instance", new MarkerProvider("instance", "instance.txt")));

        MigrationResult result = await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        temp.FileExists("instance.txt").Should().BeTrue();
    }

    [Fact]
    public async Task AddStep_with_a_version_and_providers_builds_the_step_for_you()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "Inline", new MarkerProvider("a", "a.txt"), new MarkerProvider("b", "b.txt"));

        await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        temp.FileExists("a.txt").Should().BeTrue();
        temp.FileExists("b.txt").Should().BeTrue();
    }

    [Fact]
    public async Task AddStep_of_T_resolves_the_step_from_the_container()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration().AddStep<TypeRegisteredStep>();

        await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        temp.FileExists("by-type.txt").Should().BeTrue();
    }

    [Fact]
    public async Task A_step_registered_by_type_can_take_constructor_dependencies()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddSingleton(new StorageSettings { IndexFileName = "custom.idx" });
        services.AddBarbatosMigration().AddStep<IndexStep>();

        MigrationResult result = await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        temp.FileExists("custom.idx").Should().BeTrue("the container supplied StorageSettings to the step");
    }

    [Fact]
    public async Task AddStep_with_a_factory_builds_the_step_from_the_provider()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddSingleton(new StorageSettings { IndexFileName = "from-factory.idx" });
        services.AddBarbatosMigration()
            .AddStep(sp => new MigrationStep(
                new Version(2, 0, 0),
                "From a factory",
                new MarkerProvider("factory", sp.GetRequiredService<StorageSettings>().IndexFileName)));

        await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        temp.FileExists("from-factory.idx").Should().BeTrue();
    }

    [Fact]
    public async Task AddStepsFromAssembly_registers_every_declared_step_in_the_assembly()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddSingleton(new StorageSettings());
        services.AddBarbatosMigration().AddStepsFromAssembly(typeof(ScannedStep).Assembly);

        MigrationEngine engine = services.BuildServiceProvider().GetRequiredService<MigrationEngine>();

        // Every [Migration] class in this test assembly, ordered by version by the plan.
        engine.CreatePlan().Steps.Select(step => step.TargetVersion.ToString())
            .Should().Equal(["1.5.0", "1.8.0", "1.9.0"]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        temp.FileExists("index.dat").Should().BeTrue("a scanned step still gets its dependencies injected");
        temp.FileExists("scanned.txt").Should().BeTrue();
        temp.FileExists("by-type.txt").Should().BeTrue();
    }

    [Fact]
    public void AddStepsFromAssemblyContaining_scans_the_assembly_holding_the_marker_type()
    {
        using TempDirectory temp = new();

        ServiceCollection services = Services(temp);
        services.AddSingleton(new StorageSettings());
        services.AddBarbatosMigration().AddStepsFromAssemblyContaining<ScannedStep>();

        services.Count(descriptor => descriptor.ServiceType == typeof(IMigrationStep)).Should().Be(3);
    }

    [Fact]
    public async Task UsePrompt_registers_the_service_the_engine_consults_in_manual_mode()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp, options => options.TriggerMode = UpdateTriggerMode.ManualInteractive);
        services.AddSingleton<DecliningPrompt>();
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "Asks first", new MarkerProvider("p", "never.txt"))
            .UsePrompt<DecliningPrompt>();

        ServiceProvider provider = services.BuildServiceProvider();
        MigrationResult result = await provider.GetRequiredService<MigrationEngine>().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Deferred);
        temp.FileExists("never.txt").Should().BeFalse();
        ((DecliningPrompt)provider.GetRequiredService<IUpdatePromptService>()).Asked.Should().BeTrue();
    }

    [Fact]
    public async Task UseJournal_and_UseLock_replace_the_file_based_defaults()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "Change", new MarkerProvider("p", "p.txt"))
            .UseJournal<RecordingJournal>()
            .UseLock<AlwaysAvailableLock>();

        ServiceProvider provider = services.BuildServiceProvider();
        MigrationResult result = await provider.GetRequiredService<MigrationEngine>().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        ((RecordingJournal)provider.GetRequiredService<IMigrationJournal>()).WriteCount.Should().BeGreaterThan(0);
        ((AlwaysAvailableLock)provider.GetRequiredService<IMigrationLock>()).AcquireCount.Should().Be(1);

        File.Exists(new FileMigrationJournal(temp.BackupRoot).FilePath)
            .Should().BeFalse("the replacement journal owns the record, so no file is written");
    }

    [Fact]
    public async Task AddStrategy_replaces_the_built_in_pair()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "Change", new MarkerProvider("p", "p.txt"))
            .AddStrategy<PassThroughStrategy>();

        ServiceProvider provider = services.BuildServiceProvider();
        MigrationResult result = await provider.GetRequiredService<MigrationEngine>().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        provider.GetServices<IInstallationStrategy>().Should().ContainSingle()
            .Which.Should().BeOfType<PassThroughStrategy>();

        Directory.Exists(temp.BackupRoot).Should().BeTrue("the lock still lives there");
        Directory.EnumerateDirectories(temp.BackupRoot).Should().BeEmpty("the replacement strategy takes no snapshot");
    }

    [Fact]
    public async Task Options_bind_from_the_Barbatos_Migration_configuration_section()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Barbatos:Migration:BackupRetentionCount"] = "3",
                ["Barbatos:Migration:AllowRunningOnOlderData"] = "true",
                ["Barbatos:Migration:RequiredFreeSpaceFactor"] = "1.5",
            })
            .Build();

        ServiceCollection services = Services(temp);
        services.AddSingleton(configuration);
        services.AddOptions<MigrationOptions>().Bind(configuration.GetSection(MigrationOptions.SectionName));
        services.AddBarbatosMigration().AddStep("2.0.0", "Change", new MarkerProvider("p", "p.txt"));

        ServiceProvider provider = services.BuildServiceProvider();
        MigrationOptions options = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;

        options.BackupRetentionCount.Should().Be(3);
        options.AllowRunningOnOlderData.Should().BeTrue();
        options.RequiredFreeSpaceFactor.Should().Be(1.5);
    }

    [Fact]
    public async Task The_engine_log_is_forwarded_to_ILogger()
    {
        using TempDirectory temp = new();
        temp.Stamp(new Version(1, 0, 0));

        CapturingLoggerProvider captured = new();

        ServiceCollection services = Services(temp);
        services.AddLogging(logging => logging.AddProvider(captured).SetMinimumLevel(LogLevel.Debug));
        services.AddBarbatosMigration().AddStep("2.0.0", "Change", new MarkerProvider("p", "p.txt"));

        await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        captured.Entries.Should().Contain(entry => entry.Category == "Barbatos.Migration");
        captured.Entries.Should().Contain(entry => entry.Message.Contains("Migration complete"));
    }

    [Fact]
    public void The_engine_is_a_singleton()
    {
        using TempDirectory temp = new();

        ServiceProvider provider = Services(temp).BuildServiceProvider();

        provider.GetRequiredService<MigrationEngine>()
            .Should().BeSameAs(provider.GetRequiredService<MigrationEngine>());
    }

    [Fact]
    public void Registering_the_same_version_twice_fails_when_the_engine_is_resolved()
    {
        using TempDirectory temp = new();

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "First", new MarkerProvider("a", "a.txt"))
            .AddStep("2.0.0", "Second", new MarkerProvider("b", "b.txt"));

        ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<MigrationEngine>();

        act.Should().Throw<MigrationPlanException>().WithMessage("*Two migration steps target version 2.0.0*");
    }

    [Fact]
    public async Task A_step_using_a_real_provider_package_works_through_the_container()
    {
        using TempDirectory temp = new();
        File.WriteAllText(Path.Combine(temp.Data, "settings.json"), """{ "fontSize": 14 }""");
        temp.Stamp(new Version(1, 0, 0));

        ServiceCollection services = Services(temp);
        services.AddBarbatosMigration()
            .AddStep("2.0.0", "Group the settings",
                new JsonMigrationProvider("settings.json", json => json.MoveIntoSection("fontSize", "editor")));

        await services.BuildServiceProvider().GetRequiredService<MigrationEngine>().RunAsync();

        File.ReadAllText(Path.Combine(temp.Data, "settings.json")).Should().Contain("\"editor\"");
    }

    [Fact]
    public void AddBarbatosMigration_rejects_a_null_service_collection()
    {
        Action act = () => ((IServiceCollection)null!).AddBarbatosMigration();

        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class DecliningPrompt : IUpdatePromptService
    {
        public bool Asked { get; private set; }

        public Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken)
        {
            Asked = true;
            return Task.FromResult(false);
        }
    }

    private sealed class RecordingJournal : IMigrationJournal
    {
        public int WriteCount { get; private set; }

        public MigrationJournalEntry? Read() => null;

        public void Write(MigrationJournalEntry entry) => WriteCount++;

        public void Clear()
        {
        }
    }

    private sealed class AlwaysAvailableLock : IMigrationLock
    {
        public int AcquireCount { get; private set; }

        public IDisposable? TryAcquire()
        {
            AcquireCount++;
            return new NoOpHandle();
        }

        private sealed class NoOpHandle : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Migrates the live directory with no snapshot - the shape a server-backed strategy would take.</summary>
    private sealed class PassThroughStrategy(IOptions<MigrationOptions> options) : IInstallationStrategy
    {
        private readonly MigrationOptions _options = options.Value;

        public InstallationModel Model => InstallationModel.InPlaceSingleFolder;

        public DataLocation ResolveCurrentData() =>
            new(_options.DataDirectory, new FileDataVersionStore(_options.DataDirectory).Read(), exists: true);

        public bool RequiresRunWithEmptyPlan(DataLocation currentData) => false;

        public Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress)
        {
            new FileDataVersionStore(context.WorkingDirectory).Write(context.TargetDataVersion, appliedStepIds);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress) =>
            Task.CompletedTask;

        public Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress) =>
            Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, List<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((category, formatter(state, exception)));
        }
    }
}
