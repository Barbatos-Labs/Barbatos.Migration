// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// Migrates one kind of data - a SQLite database, a settings file, a folder of assets, a
/// registry key. A <see cref="IMigrationStep"/> composes several of them so that one version
/// bump can move everything it needs to in one go.
/// </summary>
/// <remarks>
/// <para>
/// Providers do not need to be atomic or reversible in themselves: the engine's installation
/// strategy restores the whole data directory if any provider throws. What they <em>must</em>
/// be is <b>cancellation-aware</b> - check <c>cancellationToken</c> inside every loop - and
/// confined to <see cref="IMigrationContext.WorkingDirectory"/>.
/// </para>
/// <para>
/// They must also be safe to run twice. A crash after a provider finished but before the run
/// committed leaves the snapshot restored and the whole step re-run on the next launch.
/// </para>
/// </remarks>
public interface IMigrationProvider
{
    /// <summary>
    /// A short name for logs and progress UI, e.g. <c>"SQLite (app.db)"</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// This provider's share of its step's progress, relative to its siblings. A provider that
    /// rewrites a million rows should declare a much larger weight than one that renames a
    /// settings key, otherwise the progress bar will sit at 90% for most of the run. Defaults
    /// to <c>1.0</c>; must be greater than zero.
    /// </summary>
    double Weight { get; }

    /// <summary>
    /// Whether <see cref="DownAsync"/> is implemented. The engine checks this across the whole
    /// plan <em>before</em> touching any data, so an impossible downgrade fails fast instead of
    /// halfway through.
    /// </summary>
    bool CanDown { get; }

    /// <summary>Applies the change.</summary>
    /// <param name="context">The run this provider takes part in.</param>
    /// <param name="progress">Reports 0-100 for this provider alone; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Honour this inside every loop.</param>
    Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Undoes the change. Only called when <see cref="CanDown"/> is <see langword="true"/>.</summary>
    /// <param name="context">The run this provider takes part in.</param>
    /// <param name="progress">Reports 0-100 for this provider alone; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Honour this inside every loop.</param>
    Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Convenience base class for <see cref="IMigrationProvider"/>: forward-only by default, with
/// <see cref="Weight"/> at <c>1.0</c>.
/// </summary>
public abstract class MigrationProvider : IMigrationProvider
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual double Weight => 1.0;

    /// <inheritdoc />
    public virtual bool CanDown => false;

    /// <inheritdoc />
    public abstract Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken);

    /// <inheritdoc />
    public virtual Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The '{Name}' provider is forward-only; it does not implement a downgrade.");
}

/// <summary>
/// An <see cref="IMigrationProvider"/> built from delegates, for one-off transformations that
/// do not deserve a class of their own.
/// </summary>
public sealed class DelegateMigrationProvider : IMigrationProvider
{
    private readonly Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task> _up;
    private readonly Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task>? _down;

    /// <summary>Creates the provider.</summary>
    /// <param name="name">The provider name, shown in logs and progress UI.</param>
    /// <param name="up">The upgrade implementation.</param>
    /// <param name="down">The downgrade implementation, or <see langword="null"/> for forward-only.</param>
    /// <param name="weight">Relative progress weight; must be greater than zero.</param>
    public DelegateMigrationProvider(
        string name,
        Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task> up,
        Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task>? down = null,
        double weight = 1.0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A provider name is required.", nameof(name));
        if (weight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Provider weight must be greater than zero.");

        Name = name;
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

    /// <inheritdoc />
    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        _up(context, progress, cancellationToken);

    /// <inheritdoc />
    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        _down != null
            ? _down(context, progress, cancellationToken)
            : throw new NotSupportedException($"The '{Name}' provider is forward-only; it does not implement a downgrade.");
}
