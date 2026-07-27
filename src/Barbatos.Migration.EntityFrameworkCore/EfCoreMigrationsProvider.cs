// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.Migration.EntityFrameworkCore;

/// <summary>
/// Applies Entity Framework Core's own migrations as a Barbatos migration step, so they run
/// inside the snapshot the engine has already taken.
/// </summary>
/// <remarks>
/// <para>
/// EF Core's <c>Migrate()</c> is excellent at what it does, and it works against every provider
/// EF Core supports — SQL Server, PostgreSQL, MySQL, SQLite, Cosmos, Oracle. What it does not
/// do is put anything back. A migration that fails on its fourth of six migrations leaves the
/// first three applied, and the settings files, asset folders and caches that live beside the
/// database are not part of its world at all.
/// </para>
/// <para>
/// Wrapping it in a step gives it the parts it is missing: a snapshot of the whole data
/// directory taken before it starts, a journal that survives the process being killed, a
/// rollback that restores everything if it throws, real progress reporting per pending
/// migration, and a place in the same ordered plan as the JSON and file system steps that have
/// to move with it.
/// </para>
/// <para>
/// Note the scope of that promise: it covers file-backed databases, which live inside the data
/// directory the engine snapshots. For a database on a server, the snapshot cannot help - EF
/// Core's own transactional-DDL behaviour is the only protection, and it varies by provider.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.ConfigureMigration()
///        .AddStep("2.0.0", "Apply the EF Core schema migrations",
///            new EfCoreMigrationsProvider&lt;AppDbContext&gt;(
///                context => new AppDbContext(
///                    new DbContextOptionsBuilder&lt;AppDbContext&gt;()
///                        .UseSqlite($"Data Source={context.GetWorkingPath("app.db")}")
///                        .Options)));
/// </code>
/// </example>
/// <typeparam name="TContext">The application's <see cref="DbContext"/>.</typeparam>
public class EfCoreMigrationsProvider<TContext> : IMigrationProvider
    where TContext : DbContext
{
    private readonly Func<IMigrationContext, TContext> _contextFactory;

    /// <summary>Creates the provider.</summary>
    /// <param name="contextFactory">
    /// Builds the context. Given the run's context, so a file-backed database can point at
    /// <see cref="IMigrationContext.WorkingDirectory"/> - which is what makes the same step work
    /// under both installation models. The provider disposes what this returns.
    /// </param>
    /// <param name="name">A short name for logs and progress UI.</param>
    public EfCoreMigrationsProvider(Func<IMigrationContext, TContext> contextFactory, string? name = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        Name = name ?? $"EF Core ({typeof(TContext).Name})";
    }

    /// <inheritdoc />
    public virtual string Name { get; }

    /// <summary>Defaults to <c>5.0</c>: applying a batch of EF migrations is rarely the quick part of a step.</summary>
    public virtual double Weight { get; set; } = 5.0;

    /// <summary>
    /// Engine-specific cleanup after the migration. <b>Set this to
    /// <see cref="DatabaseDialects.Sqlite"/> for a file-backed database</b> - disposing a
    /// <see cref="DbContext"/> returns its connection to the driver's pool rather than closing
    /// the file, and a handle still open on the <c>.db</c> makes the migration engine's
    /// snapshot, restore and directory rename fail. Defaults to
    /// <see cref="DatabaseDialects.Generic"/>, which is correct for a server database.
    /// </summary>
    public IDatabaseDialect Dialect { get; set; } = DatabaseDialects.Generic;

    /// <summary>Options passed to <see cref="Dialect"/>.</summary>
    public DatabaseMigrationOptions DialectOptions { get; } = new();

    /// <summary>
    /// Always <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// EF Core can migrate down to a named target, but a step does not know which migration was
    /// current before it ran, and guessing would be worse than refusing. Use
    /// <see cref="EfCoreDowngradeMigrationsProvider{TContext}"/> when you know the target
    /// migration by name, or the side-by-side installation model, where a downgrade is just
    /// launching the older build.
    /// </remarks>
    public virtual bool CanDown => false;

    /// <inheritdoc />
    public virtual async Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TContext dbContext = _contextFactory(context);

        try
        {
            progress?.Report(new MigrationProgress(0, "Checking for pending schema migrations..."));

            List<string> pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (pending.Count == 0)
            {
                context.Logger.Log(MigrationLogLevel.Debug, $"{Name}: no pending EF Core migrations.");
                progress?.Report(new MigrationProgress(100, "Schema is already up to date"));
                return;
            }

            context.Logger.Log(
                MigrationLogLevel.Information,
                $"{Name}: applying {pending.Count} EF Core migration(s): {string.Join(", ", pending)}");

            // EF Core's MigrateAsync applies the whole batch in one call with no per-migration
            // callback, so the honest report is the list of names and an indeterminate bar rather
            // than a percentage invented from nothing.
            progress?.Report(new MigrationProgress(
                0,
                pending.Count == 1
                    ? $"Applying schema migration {pending[0]}"
                    : $"Applying {pending.Count} schema migrations",
                isIndeterminate: true));

            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new MigrationProgress(100, $"Applied {pending.Count} schema migration(s)"));
        }
        finally
        {
            // On every path out, including a failure - the rollback that follows one needs the
            // file handle gone even more than the success path does.
            await EfCoreConnectionRelease.ReleaseAsync(dbContext, Dialect, DialectOptions, context.Logger).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public virtual Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"'{Name}' is forward-only. Use EfCoreDowngradeMigrationsProvider<{typeof(TContext).Name}> with an explicit " +
            "target migration name, or the side-by-side installation model where downgrading means running the older build.");
}

/// <summary>
/// The reversible counterpart of <see cref="EfCoreMigrationsProvider{TContext}"/>: migrates up
/// to one named EF Core migration and back down to another.
/// </summary>
/// <remarks>
/// Naming both ends explicitly is what makes the downgrade well-defined. EF Core's own
/// <c>Migrate(targetMigration)</c> will happily migrate in either direction, but only if it is
/// told where to stop - and a step that has to infer that from the current state would be
/// guessing at the one moment guessing is least acceptable.
/// </remarks>
/// <typeparam name="TContext">The application's <see cref="DbContext"/>.</typeparam>
public sealed class EfCoreDowngradeMigrationsProvider<TContext> : EfCoreMigrationsProvider<TContext>
    where TContext : DbContext
{
    private readonly Func<IMigrationContext, TContext> _contextFactory;
    private readonly string _upTargetMigration;
    private readonly string _downTargetMigration;

    /// <summary>Creates the provider.</summary>
    /// <param name="contextFactory">Builds the context; the provider disposes what it returns.</param>
    /// <param name="upTargetMigration">The EF Core migration to end at when upgrading.</param>
    /// <param name="downTargetMigration">
    /// The EF Core migration to return to when downgrading. Pass <c>"0"</c> to undo every
    /// migration.
    /// </param>
    /// <param name="name">A short name for logs and progress UI.</param>
    public EfCoreDowngradeMigrationsProvider(
        Func<IMigrationContext, TContext> contextFactory,
        string upTargetMigration,
        string downTargetMigration,
        string? name = null)
        : base(contextFactory, name)
    {
        if (string.IsNullOrWhiteSpace(upTargetMigration))
            throw new ArgumentException("An upgrade target migration name is required.", nameof(upTargetMigration));
        if (string.IsNullOrWhiteSpace(downTargetMigration))
            throw new ArgumentException("A downgrade target migration name is required.", nameof(downTargetMigration));

        _contextFactory = contextFactory;
        _upTargetMigration = upTargetMigration;
        _downTargetMigration = downTargetMigration;
    }

    /// <inheritdoc />
    public override bool CanDown => true;

    /// <inheritdoc />
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        MigrateToAsync(context, _upTargetMigration, progress, cancellationToken);

    /// <inheritdoc />
    public override Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        MigrateToAsync(context, _downTargetMigration, progress, cancellationToken);

    private async Task MigrateToAsync(
        IMigrationContext context,
        string targetMigration,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TContext dbContext = _contextFactory(context);

        try
        {
            context.Logger.Log(MigrationLogLevel.Information, $"{Name}: migrating schema to '{targetMigration}'.");

            progress?.Report(new MigrationProgress(0, $"Migrating schema to {targetMigration}", isIndeterminate: true));

            IMigrator migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(targetMigration, cancellationToken).ConfigureAwait(false);

            progress?.Report(new MigrationProgress(100, $"Schema is at {targetMigration}"));
        }
        finally
        {
            await EfCoreConnectionRelease.ReleaseAsync(dbContext, Dialect, DialectOptions, context.Logger).ConfigureAwait(false);
        }
    }
}
