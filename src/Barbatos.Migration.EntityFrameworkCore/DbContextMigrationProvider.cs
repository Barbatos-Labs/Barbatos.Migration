// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Barbatos.Migration.EntityFrameworkCore;

/// <summary>
/// Transforms <em>data</em> through a <see cref="DbContext"/>, as opposed to
/// <see cref="EfCoreMigrationsProvider{TContext}"/>, which changes the <em>schema</em>.
/// </summary>
/// <remarks>
/// <para>
/// This is for the half of a migration EF Core's own migrations are bad at: splitting a full
/// name into two columns, normalising stored durations, deriving a new lookup table from
/// existing rows. Doing that in raw SQL inside a migration file means hand-writing what the
/// model already describes; doing it here means writing ordinary LINQ against the entities.
/// </para>
/// <para>
/// The delegate runs inside a transaction opened on the context, so a failure part-way rolls
/// back the data changes as a unit — and the migration engine's snapshot rolls back everything
/// else that had already moved.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// new DbContextMigrationProvider&lt;AppDbContext&gt;(
///     "Split Users.FullName",
///     contextFactory,
///     up: async (db, progress, ct) =>
///     {
///         List&lt;User&gt; users = await db.Users.Where(u => u.FirstName == null).ToListAsync(ct);
///         for (int i = 0; i &lt; users.Count; i++)
///         {
///             string[] parts = users[i].FullName.Split(' ', 2);
///             users[i].FirstName = parts[0];
///             users[i].LastName  = parts.Length > 1 ? parts[1] : string.Empty;
///             progress?.Report(new MigrationProgress(i * 100.0 / users.Count, $"Migrating user {i + 1} of {users.Count}"));
///         }
///
///         await db.SaveChangesAsync(ct);
///     });
/// </code>
/// </example>
/// <typeparam name="TContext">The application's <see cref="DbContext"/>.</typeparam>
public class DbContextMigrationProvider<TContext> : IMigrationProvider
    where TContext : DbContext
{
    private readonly Func<IMigrationContext, TContext> _contextFactory;
    private readonly Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task> _up;
    private readonly Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task>? _down;

    /// <summary>Creates the provider.</summary>
    /// <param name="name">A short name for logs and progress UI.</param>
    /// <param name="contextFactory">Builds the context; the provider disposes what it returns.</param>
    /// <param name="up">The transformation. Call <c>SaveChangesAsync</c> yourself.</param>
    /// <param name="down">The inverse, or <see langword="null"/> for forward-only.</param>
    /// <param name="weight">Relative progress weight; must be greater than zero.</param>
    public DbContextMigrationProvider(
        string name,
        Func<IMigrationContext, TContext> contextFactory,
        Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task> up,
        Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task>? down = null,
        double weight = 3.0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A provider name is required.", nameof(name));
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Provider weight must be greater than zero.");

        Name = name;
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _up = up ?? throw new ArgumentNullException(nameof(up));
        _down = down;
        Weight = weight;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public double Weight { get; }

    /// <inheritdoc />
    public bool CanDown => _down != null;

    /// <summary>
    /// Whether the transformation runs inside a transaction opened on the context. Defaults to
    /// <see langword="true"/>. Turn it off only for a provider that manages its own batching -
    /// rewriting several million rows in one transaction can exhaust the database's log.
    /// </summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>
    /// Engine-specific cleanup after the transformation. Set it to
    /// <see cref="DatabaseDialects.Sqlite"/> for a file-backed database, so the file handle is
    /// gone before the migration engine needs to rename the directory it sits in.
    /// </summary>
    public IDatabaseDialect Dialect { get; set; } = DatabaseDialects.Generic;

    /// <summary>Options passed to <see cref="Dialect"/>.</summary>
    public DatabaseMigrationOptions DialectOptions { get; } = new();

    /// <inheritdoc />
    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        RunAsync(context, _up, progress, cancellationToken);

    /// <inheritdoc />
    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        _down != null
            ? RunAsync(context, _down, progress, cancellationToken)
            : throw new NotSupportedException($"'{Name}' is forward-only; it does not implement a downgrade.");

    private async Task RunAsync(
        IMigrationContext context,
        Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task> work,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TContext dbContext = _contextFactory(context);

        try
        {
            if (!UseTransaction)
            {
                await work(dbContext, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new MigrationProgress(100, $"{Name} complete"));
                return;
            }

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await work(dbContext, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Committed without the token: the work is already done, and abandoning the commit
            // would discard it for no safety benefit.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            progress?.Report(new MigrationProgress(100, $"{Name} complete"));
        }
        finally
        {
            await EfCoreConnectionRelease.ReleaseAsync(dbContext, Dialect, DialectOptions, context.Logger).ConfigureAwait(false);
        }
    }
}
