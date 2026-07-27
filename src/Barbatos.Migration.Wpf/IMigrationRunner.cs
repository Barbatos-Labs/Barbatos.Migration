// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Wpf.Dispatching;
using Microsoft.Extensions.Options;

namespace Barbatos.Migration.Wpf;

/// <summary>
/// Runs the migration from a WPF startup path: off the UI thread, with progress marshalled back
/// onto it.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>What <see cref="RunAsync"/> would do. Cheap; safe to call before showing UI.</summary>
    MigrationPlan CreatePlan();

    /// <summary>
    /// Runs any outstanding migration.
    /// </summary>
    /// <param name="progress">
    /// Receives progress already marshalled onto the UI thread, so it can be bound to directly.
    /// </param>
    /// <param name="cancellationToken">Cancels the migration; the engine then restores the data.</param>
    Task<MigrationResult> RunAsync(IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IMigrationRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two things this does that calling the engine directly from <c>OnStartup</c> would not.
/// </para>
/// <para>
/// It runs the engine on a thread-pool thread. Copying a data directory is synchronous,
/// disk-bound work; run it on the UI thread and the splash screen freezes solid - no animation,
/// no progress bar movement, no Cancel button - for exactly the minutes during which the user
/// most needs to see that something is happening.
/// </para>
/// <para>
/// And it marshals progress back through <see cref="IDispatcher"/>, throttled. A provider
/// rewriting a hundred thousand rows can report thousands of times a second; forwarding each
/// one to the UI thread would flood the dispatcher queue and make the window <em>less</em>
/// responsive the more diligently the provider reports.
/// </para>
/// </remarks>
public sealed class MigrationRunner : IMigrationRunner
{
    private static readonly TimeSpan MinimumReportInterval = TimeSpan.FromMilliseconds(50);

    private readonly MigrationEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly MigrationOptions _options;

    /// <summary>Creates the runner.</summary>
    public MigrationRunner(MigrationEngine engine, IDispatcher dispatcher, IOptions<MigrationOptions> options)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    /// <inheritdoc />
    public MigrationPlan CreatePlan() => _engine.CreatePlan();

    /// <inheritdoc />
    public Task<MigrationResult> RunAsync(IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IProgress<MigrationProgress>? relay = progress == null ? null : new DispatchedProgress(progress, _dispatcher);

        // Task.Run rather than a bare await: the engine's file copying is synchronous, so
        // awaiting it on the UI thread would still block the UI thread.
        return Task.Run(() => _engine.RunAsync(relay, cancellationToken), cancellationToken);
    }

    /// <summary>The options in force, for callers that need to explain what is about to happen.</summary>
    public MigrationOptions Options => _options;

    private sealed class DispatchedProgress : IProgress<MigrationProgress>
    {
        private readonly IProgress<MigrationProgress> _inner;
        private readonly IDispatcher _dispatcher;
        private readonly object _gate = new();

        private DateTime _lastDispatchUtc = DateTime.MinValue;
        private double _lastPercentage = double.MinValue;
        private string _lastDetail = string.Empty;

        public DispatchedProgress(IProgress<MigrationProgress> inner, IDispatcher dispatcher)
        {
            _inner = inner;
            _dispatcher = dispatcher;
        }

        public void Report(MigrationProgress value)
        {
            if (!ShouldDispatch(value))
                return;

            if (!_dispatcher.IsDispatchRequired)
            {
                _inner.Report(value);
                return;
            }

            _dispatcher.Dispatch(() => _inner.Report(value));
        }

        private bool ShouldDispatch(MigrationProgress value)
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;

                // A terminal or near-terminal report always gets through, whatever the
                // throttle says - a progress bar stuck at 99.4% because the last report was
                // dropped looks exactly like a hang.
                bool important = value.Percentage >= 99.9
                    || value.Phase is MigrationPhase.Completed or MigrationPhase.RollingBack or MigrationPhase.Committing;

                bool changedEnough = value.Percentage - _lastPercentage >= 0.5
                    || !string.Equals(value.Detail, _lastDetail, StringComparison.Ordinal);

                if (!important && (!changedEnough || now - _lastDispatchUtc < MinimumReportInterval))
                    return false;

                _lastDispatchUtc = now;
                _lastPercentage = value.Percentage;
                _lastDetail = value.Detail;
                return true;
            }
        }
    }
}
