// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration.Internal;

/// <summary>
/// Rescales a component's 0-100 progress into a slice of the overall run, and guarantees the
/// result never moves backwards.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does <em>not</em> derive from <see cref="Progress{T}"/>. That class posts
/// through the captured <see cref="SynchronizationContext"/>, which on a UI thread means
/// reports arrive asynchronously, can be reordered, and can still be delivered after the
/// migration has finished and the window has moved on. Relaying synchronously keeps the
/// ordering the providers actually produced; marshalling to the UI thread is the caller's
/// business, and their own <see cref="Progress{T}"/> (or the Barbatos dispatcher) does it once,
/// at the edge.
/// </para>
/// <para>
/// The monotonic clamp matters more than it looks. A step whose second provider starts
/// reporting from 0 would otherwise yank the bar backwards on every provider boundary, and
/// users read a progress bar that jumps back as a sign the app is broken.
/// </para>
/// </remarks>
internal sealed class ProgressRelay : IProgress<MigrationProgress>
{
    private readonly IProgress<MigrationProgress>? _inner;
    private readonly object _gate = new();
    private double _highWaterMark;

    public ProgressRelay(IProgress<MigrationProgress>? inner)
    {
        _inner = inner;
    }

    /// <summary>The lower bound of the slice being reported into.</summary>
    public double Offset { get; set; }

    /// <summary>The width of the slice being reported into.</summary>
    public double Span { get; set; } = 100.0;

    /// <summary>Context stamped onto every report passing through.</summary>
    public MigrationPhase Phase { get; set; } = MigrationPhase.Planning;

    /// <inheritdoc cref="MigrationProgress.StepDescription" />
    public string StepDescription { get; set; } = string.Empty;

    /// <inheritdoc cref="MigrationProgress.ProviderName" />
    public string ProviderName { get; set; } = string.Empty;

    /// <inheritdoc cref="MigrationProgress.TargetVersion" />
    public Version? TargetVersion { get; set; }

    /// <inheritdoc />
    public void Report(MigrationProgress value)
    {
        double local = value.Percentage;
        if (double.IsNaN(local) || double.IsInfinity(local))
            local = 0;

        local = local < 0 ? 0 : local > 100 ? 100 : local;

        double overall = Offset + (local * Span / 100.0);

        lock (_gate)
        {
            if (overall < _highWaterMark)
                overall = _highWaterMark;
            else
                _highWaterMark = overall;
        }

        Emit(new MigrationProgress(
            value.Phase == MigrationPhase.Migrating ? Phase : value.Phase,
            overall,
            value.Detail,
            value.StepDescription.Length > 0 ? value.StepDescription : StepDescription,
            value.ProviderName.Length > 0 ? value.ProviderName : ProviderName,
            value.TargetVersion ?? TargetVersion,
            value.IsIndeterminate));
    }

    /// <summary>Emits a report the engine composes itself, bypassing the rescale.</summary>
    public void ReportPhase(MigrationPhase phase, double overallPercentage, string detail)
    {
        lock (_gate)
        {
            if (overallPercentage < _highWaterMark)
                overallPercentage = _highWaterMark;
            else
                _highWaterMark = overallPercentage;
        }

        Emit(new MigrationProgress(phase, overallPercentage, detail, StepDescription, ProviderName, TargetVersion, isIndeterminate: false));
    }

    /// <summary>Emits a terminal report, ignoring the high-water mark.</summary>
    public void ReportFinal(MigrationPhase phase, double overallPercentage, string detail)
    {
        lock (_gate)
        {
            _highWaterMark = overallPercentage;
        }

        Emit(new MigrationProgress(phase, overallPercentage, detail, string.Empty, string.Empty, null, isIndeterminate: false));
    }

    private void Emit(MigrationProgress progress)
    {
        if (_inner == null)
            return;

        try
        {
            _inner.Report(progress);
        }
        catch (Exception)
        {
            // A progress handler that throws - a data-binding error on a UI thread, say - must
            // not abort a migration that is otherwise going fine, and must certainly not be
            // mistaken for a provider failure and trigger a rollback.
        }
    }
}
