// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// A single progress report. Providers report their own 0-100 <see cref="Percentage"/> and a
/// human-readable <see cref="Detail"/>; the engine rescales that into the overall percentage
/// and fills in the surrounding step/phase information before handing it to the caller.
/// </summary>
/// <remarks>
/// This is a <see langword="readonly struct"/> on purpose: a long-running provider can report
/// thousands of times, and progress reports must not add GC pressure to a migration that is
/// already competing with heavy disk I/O.
/// </remarks>
public readonly struct MigrationProgress
{
    /// <summary>
    /// Creates a provider-level report. The engine fills in everything else.
    /// </summary>
    /// <param name="percentage">How far this provider has got, from 0 to 100.</param>
    /// <param name="detail">A human-readable description of what is happening right now.</param>
    public MigrationProgress(double percentage, string? detail = null)
        : this(percentage, detail, isIndeterminate: false)
    {
    }

    /// <summary>
    /// Creates a provider-level report for work whose remaining duration cannot be measured -
    /// a single long call into a third-party library, say, that offers no callback of its own.
    /// The UI shows a marquee instead of inventing a percentage that would only mislead.
    /// </summary>
    /// <param name="percentage">How far this provider has got, from 0 to 100. Ignored when <paramref name="isIndeterminate"/> is <see langword="true"/>.</param>
    /// <param name="detail">A human-readable description of what is happening right now.</param>
    /// <param name="isIndeterminate">Whether the remaining work can be measured.</param>
    public MigrationProgress(double percentage, string? detail, bool isIndeterminate)
        : this(MigrationPhase.Migrating, percentage, detail, isIndeterminate)
    {
    }

    /// <summary>
    /// Creates a report for a given phase.
    /// </summary>
    /// <remarks>
    /// Providers do not need this - the engine stamps <see cref="MigrationPhase.Migrating"/> on
    /// their reports for them. An <see cref="IInstallationStrategy"/> does: it is the thing that
    /// runs during <see cref="MigrationPhase.Preparing"/> and
    /// <see cref="MigrationPhase.RollingBack"/>, and a progress bar that cannot tell those apart
    /// from ordinary migration work cannot disable its Cancel button at the right moment.
    /// </remarks>
    /// <param name="phase">What is happening at a coarse level.</param>
    /// <param name="percentage">How far this phase has got, from 0 to 100.</param>
    /// <param name="detail">A human-readable description of what is happening right now.</param>
    /// <param name="isIndeterminate">Whether the remaining work can be measured.</param>
    public MigrationProgress(MigrationPhase phase, double percentage, string? detail = null, bool isIndeterminate = false)
    {
        Phase = phase;
        Percentage = percentage;
        Detail = detail ?? string.Empty;
        StepDescription = string.Empty;
        ProviderName = string.Empty;
        TargetVersion = null;
        IsIndeterminate = isIndeterminate;
    }

    internal MigrationProgress(
        MigrationPhase phase,
        double percentage,
        string detail,
        string stepDescription,
        string providerName,
        Version? targetVersion,
        bool isIndeterminate)
    {
        Phase = phase;
        Percentage = percentage;
        Detail = detail;
        StepDescription = stepDescription;
        ProviderName = providerName;
        TargetVersion = targetVersion;
        IsIndeterminate = isIndeterminate;
    }

    /// <summary>What the engine is doing at a coarse level.</summary>
    public MigrationPhase Phase { get; }

    /// <summary>
    /// Progress from 0 to 100. On reports the engine hands to the caller this is monotonic -
    /// it never moves backwards within a run - so it can be bound straight to a progress bar.
    /// Meaningless when <see cref="IsIndeterminate"/> is <see langword="true"/>.
    /// </summary>
    public double Percentage { get; }

    /// <summary>
    /// <see langword="true"/> when the remaining work cannot be measured (for example a
    /// provider that reports no progress at all), so the UI should show a marquee instead of a
    /// filled bar.
    /// </summary>
    public bool IsIndeterminate { get; }

    /// <summary>A human-readable description of the current unit of work.</summary>
    public string Detail { get; }

    /// <summary><see cref="IMigrationStep.Description"/> of the running step, if any.</summary>
    public string StepDescription { get; }

    /// <summary><see cref="IMigrationProvider.Name"/> of the running provider, if any.</summary>
    public string ProviderName { get; }

    /// <summary>The version the running step migrates to, if any.</summary>
    public Version? TargetVersion { get; }

    /// <inheritdoc />
    public override string ToString() =>
        ProviderName.Length == 0 ? $"[{Phase}] {Detail}" : $"[{Phase}/{ProviderName}] {Detail}";
}

/// <summary>
/// The coarse stage a migration run is in. Useful for UI that wants to show more than a
/// percentage - notably to disable the Cancel button once <see cref="Committing"/> or
/// <see cref="RollingBack"/> is reached, since neither can be cancelled.
/// </summary>
public enum MigrationPhase
{
    /// <summary>Checking versions and building the plan. Cheap and fast.</summary>
    Planning,

    /// <summary>
    /// Recovering from an earlier run that was killed before it finished. Runs before anything
    /// else and cannot be cancelled.
    /// </summary>
    Recovering,

    /// <summary>
    /// Taking the snapshot (in-place) or cloning the previous version's folder (side-by-side).
    /// Usually the longest phase for large data sets, and cancellable.
    /// </summary>
    Preparing,

    /// <summary>Running the migration steps. Cancellable.</summary>
    Migrating,

    /// <summary>
    /// Publishing the result and stamping the new data version. Not cancellable - a
    /// cancellation request arriving here is honoured only after the commit completes.
    /// </summary>
    Committing,

    /// <summary>Undoing a failed or cancelled run. Not cancellable.</summary>
    RollingBack,

    /// <summary>The run has finished, successfully or not.</summary>
    Completed,
}
