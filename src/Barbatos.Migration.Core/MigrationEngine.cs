// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using Barbatos.Migration.Internal;
using Barbatos.Migration.Strategies;

namespace Barbatos.Migration;

/// <summary>
/// Runs migrations. One instance per application; call <see cref="RunAsync"/> once during
/// startup, before anything opens the data.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns the ordering and the safety guarantees; it never touches the file system
/// itself. Where the data lives, how it is protected and what "undo" means all belong to the
/// <see cref="IInstallationStrategy"/>, and what actually changes belongs to the
/// <see cref="IMigrationProvider"/>s. That split is what lets a new installation model or a new
/// data store be added without reopening this class.
/// </para>
/// <para>
/// Every run is bracketed by four things, in this order: a cross-process lock, recovery of any
/// run that was killed before it finished, a journal entry written before the first byte
/// changes, and a version stamp written as the last act of a successful commit. Remove any one
/// of them and there is a realistic sequence of events - two app instances, a power cut, a
/// force-quit - that ends with the user's data in a state nothing can reason about.
/// </para>
/// </remarks>
public sealed class MigrationEngine
{
    // The overall progress bar is split into fixed slices. Preparation gets a generous share
    // because on a large data set the snapshot really does dominate; commit gets a token slice
    // so the bar does not sit at 100% while the last rename happens.
    private const double PrepareShare = 20.0;
    private const double MigrateShare = 77.0;
    private const double CommitShare = 3.0;

    private readonly MigrationOptions _options;
    private readonly IReadOnlyList<IMigrationStep> _steps;
    private readonly IReadOnlyDictionary<InstallationModel, IInstallationStrategy> _strategies;
    private readonly IMigrationJournal _journal;
    private readonly IMigrationLock _lock;
    private readonly IUpdatePromptService? _promptService;
    private readonly IMigrationLogger _logger;

    /// <summary>Creates an engine.</summary>
    /// <param name="options">How the engine should behave. Validated here, so a misconfiguration fails at startup rather than mid-migration.</param>
    /// <param name="steps">Every migration step the application has ever shipped, in any order.</param>
    /// <param name="strategies">The installation strategies; defaults to the two built-in ones.</param>
    /// <param name="journal">The crash journal; defaults to a file in the backup root.</param>
    /// <param name="migrationLock">The cross-process lock; defaults to a file in the backup root.</param>
    /// <param name="promptService">Consulted under <see cref="UpdateTriggerMode.ManualInteractive"/>.</param>
    public MigrationEngine(
        MigrationOptions options,
        IEnumerable<IMigrationStep> steps,
        IEnumerable<IInstallationStrategy>? strategies = null,
        IMigrationJournal? journal = null,
        IMigrationLock? migrationLock = null,
        IUpdatePromptService? promptService = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _steps = steps == null ? [] : [.. steps];
        _logger = options.Logger ?? NullMigrationLogger.Instance;

        Dictionary<InstallationModel, IInstallationStrategy> map = [];
        foreach (IInstallationStrategy strategy in strategies ?? DefaultStrategies(options))
            map[strategy.Model] = strategy;

        if (!map.ContainsKey(options.Model))
        {
            throw new MigrationException(
                $"No installation strategy is registered for {options.Model}. " +
                "Register one, or leave MigrationEngine's 'strategies' argument null to use the built-in pair.");
        }

        _strategies = map;
        _journal = journal ?? new FileMigrationJournal(options.BackupRootDirectory);
        _lock = migrationLock ?? new FileMigrationLock(options.BackupRootDirectory);
        _promptService = promptService;

        // Fail fast on a broken step set: duplicate versions and duplicate ids are
        // configuration mistakes, and finding them at startup beats finding them on the one
        // machine that happens to be three versions behind. Only the version-independent checks
        // run here - whether a downgrade is possible depends on the versions actually involved,
        // which are not known until the run.
        _ = MigrationPlan.ValidateSteps(_steps);
    }

    /// <summary>The steps this engine knows about.</summary>
    public IReadOnlyList<IMigrationStep> Steps => _steps;

    /// <summary>
    /// Works out what <see cref="RunAsync"/> would do, without touching anything. Reads the
    /// version stamp and nothing else, so it is safe to call from a settings screen or a
    /// diagnostics command.
    /// </summary>
    public MigrationPlan CreatePlan()
    {
        IInstallationStrategy strategy = _strategies[_options.Model];
        DataLocation location = strategy.ResolveCurrentData();
        Version current = ResolveCurrentVersion(location);

        return MigrationPlan.Create(_steps, current, _options.TargetDataVersion);
    }

    /// <summary>
    /// Brings the data up to <see cref="MigrationOptions.TargetDataVersion"/>.
    /// </summary>
    /// <param name="progress">
    /// Receives overall 0-100 progress. Reported synchronously from whichever thread the work
    /// is on, so wrap it in a <see cref="Progress{T}"/> (or the platform dispatcher) if it
    /// touches UI.
    /// </param>
    /// <param name="cancellationToken">
    /// Honoured during preparation and while steps run. Once the commit starts, cancellation is
    /// ignored - stopping there would cost more than finishing.
    /// </param>
    /// <returns>
    /// Never throws for an ordinary failure: a step that blows up produces
    /// <see cref="MigrationOutcome.Failed"/> with the data restored, and the exception is in
    /// <see cref="MigrationResult.Error"/>. Inspect <see cref="MigrationResult.Outcome"/> and
    /// <see cref="MigrationResult.CanContinue"/> rather than assuming success.
    /// </returns>
    public async Task<MigrationResult> RunAsync(
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProgressRelay relay = new(progress);
        IInstallationStrategy strategy = _strategies[_options.Model];

        DirectoryOperations.Ensure(_options.BackupRootDirectory);

        // One migration at a time, across every process that can reach this data.
        IDisposable? lockHandle = _lock.TryAcquire();
        if (lockHandle == null)
        {
            _logger.Log(MigrationLogLevel.Warning, "Another process is already migrating this data directory.");
            return Blocked(
                new MigrationLockException(
                    "Another instance of the application is already updating your data. " +
                    "Wait for it to finish, or close it and try again."),
                _options.TargetDataVersion,
                _options.TargetDataVersion,
                _options.DataDirectory,
                stopwatch.Elapsed);
        }

        using (lockHandle)
        {
            try
            {
                await RecoverIfNeededAsync(relay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log(MigrationLogLevel.Critical, "Could not recover from an interrupted migration.", ex);
                return Blocked(ex, _options.TargetDataVersion, _options.TargetDataVersion, _options.DataDirectory, stopwatch.Elapsed);
            }

            relay.ReportPhase(MigrationPhase.Planning, 0, "Checking your data...");

            DataLocation location = strategy.ResolveCurrentData();
            Version current = ResolveCurrentVersion(location);
            Version target = _options.TargetDataVersion;

            if (current > target && (!_options.AllowDowngrade || _options.Model != InstallationModel.InPlaceSingleFolder))
            {
                MigrationException error = new(
                    $"Your data is at version {current}, which is newer than this build ({target}). " +
                    (_options.Model == InstallationModel.SideBySideMultiFolder
                        ? "Run the newer version of the application instead."
                        : "Install the newer version again, or enable MigrationOptions.AllowDowngrade if every step can be undone."));

                _logger.Log(MigrationLogLevel.Error, error.Message);
                return Blocked(error, current, target, location.Directory, stopwatch.Elapsed);
            }

            MigrationPlan plan;
            try
            {
                plan = MigrationPlan.Create(_steps, current, target);
            }
            catch (MigrationPlanException ex)
            {
                _logger.Log(MigrationLogLevel.Error, "The registered migration steps cannot produce a valid plan.", ex);
                return Blocked(ex, current, target, location.Directory, stopwatch.Elapsed);
            }

            if (plan.IsEmpty && !strategy.RequiresRunWithEmptyPlan(location))
            {
                // No step stands between the data and the target, so the data is already in the
                // shape this build expects. Record that, both so an unstamped directory stops
                // being guessed at and so the stamp does not lag behind forever on an
                // application that ships versions without schema changes.
                Version settled = current < target ? target : current;
                if (location.Exists && location.Version != settled)
                    TryStamp(location.Directory, settled, []);

                _logger.Log(MigrationLogLevel.Information, $"Data is already at {current}; nothing to migrate.");
                relay.ReportFinal(MigrationPhase.Completed, 100, "Your data is up to date.");

                return new MigrationResult(
                    MigrationOutcome.UpToDate, current, settled, target, [], location.Directory, null, stopwatch.Elapsed, null, null)
                {
                    CanContinue = true,
                };
            }

            _logger.Log(MigrationLogLevel.Information, plan.Describe());

            if (_options.TriggerMode == UpdateTriggerMode.ManualInteractive && _promptService != null)
            {
                bool proceed = await AskUserAsync(plan, location, cancellationToken).ConfigureAwait(false);
                if (!proceed)
                {
                    _logger.Log(MigrationLogLevel.Information, "The user postponed the migration.");
                    relay.ReportFinal(MigrationPhase.Completed, 0, "Update postponed.");

                    return new MigrationResult(
                        MigrationOutcome.Deferred, current, current, target, [], location.Directory, null, stopwatch.Elapsed, null, null)
                    {
                        CanContinue = _options.AllowRunningOnOlderData,
                    };
                }
            }

            return await ExecuteAsync(strategy, plan, location, current, target, relay, stopwatch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MigrationResult> ExecuteAsync(
        IInstallationStrategy strategy,
        MigrationPlan plan,
        DataLocation location,
        Version current,
        Version target,
        ProgressRelay relay,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        string sessionId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        MigrationContext context = new(
            sessionId, current, target, plan.Direction, _options.Model, location.Directory, _logger);

        MigrationJournalEntry journalEntry = new(
            sessionId,
            DateTimeOffset.UtcNow,
            _options.Model,
            plan.Direction,
            current,
            target,
            location.Directory,
            location.Directory,
            backupDirectory: null,
            MigrationPhase.Preparing,
            lastCompletedStepId: null);

        List<AppliedStep> applied = [];
        Exception? failure = null;
        bool canceled = false;

        try
        {
            // Written before anything changes. From here until the journal is cleared, any
            // launch that finds this file knows a migration was in flight and recovers.
            _journal.Write(journalEntry);

            relay.Offset = 0;
            relay.Span = PrepareShare;
            relay.Phase = MigrationPhase.Preparing;
            relay.ReportPhase(MigrationPhase.Preparing, 0, "Preparing to update your data...");

            await strategy.PrepareAsync(context, relay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            journalEntry = WithWorkingDirectories(journalEntry, context, MigrationPhase.Migrating);
            _journal.Write(journalEntry);

            await RunStepsAsync(plan, context, applied, relay, journalEntry, cancellationToken).ConfigureAwait(false);

            journalEntry.Phase = MigrationPhase.Committing;
            _journal.Write(journalEntry);

            relay.Offset = PrepareShare + MigrateShare;
            relay.Span = CommitShare;
            relay.Phase = MigrationPhase.Committing;

            // Deliberately not passing the token: abandoning a run at the commit boundary
            // would throw away all the work for no safety benefit.
            await strategy.CommitAsync(context, [.. applied.Select(step => step.Id)], relay).ConfigureAwait(false);

            _journal.Clear();

            _logger.Log(MigrationLogLevel.Information, $"Migration complete: {current} -> {target} in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            relay.ReportFinal(MigrationPhase.Completed, 100, "Your data is up to date.");

            return new MigrationResult(
                MigrationOutcome.Succeeded, current, target, target, applied,
                context.WorkingDirectory, context.BackupDirectory, stopwatch.Elapsed, null, null)
            {
                CanContinue = true,
            };
        }
        catch (OperationCanceledException ex)
        {
            canceled = true;
            failure = ex;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Rollback runs outside the catch block so that its own failure is not caught by a
        // later catch of the same try, and so the cancellation token - which by definition may
        // already be cancelled - cannot reach it.
        return await RollBackAsync(strategy, context, applied, current, target, failure, canceled, relay, stopwatch).ConfigureAwait(false);
    }

    private async Task RunStepsAsync(
        MigrationPlan plan,
        MigrationContext context,
        List<AppliedStep> applied,
        ProgressRelay relay,
        MigrationJournalEntry journalEntry,
        CancellationToken cancellationToken)
    {
        double totalWeight = plan.Steps.Sum(step => step.Providers.Sum(provider => provider.Weight));
        if (totalWeight <= 0)
            totalWeight = 1;

        double completedWeight = 0;
        bool up = plan.Direction == MigrationDirection.Upgrade;

        foreach (IMigrationStep step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Stopwatch stepWatch = Stopwatch.StartNew();
            _logger.Log(MigrationLogLevel.Information, $"{(up ? "Applying" : "Reverting")} {step.TargetVersion}: {step.Description}");

            // Within a step providers run one after another, never concurrently: two of them
            // rewriting the same directory at once is precisely the corruption this framework
            // exists to prevent.
            foreach (IMigrationProvider provider in step.Providers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                relay.Offset = PrepareShare + (completedWeight / totalWeight * MigrateShare);
                relay.Span = provider.Weight / totalWeight * MigrateShare;
                relay.Phase = MigrationPhase.Migrating;
                relay.StepDescription = step.Description;
                relay.ProviderName = provider.Name;
                relay.TargetVersion = step.TargetVersion;

                relay.ReportPhase(MigrationPhase.Migrating, relay.Offset, $"{step.Description} - {provider.Name}");

                _logger.Log(MigrationLogLevel.Debug, $"  {provider.Name} ({(up ? "up" : "down")})");

                if (up)
                    await provider.UpAsync(context, relay, cancellationToken).ConfigureAwait(false);
                else
                    await provider.DownAsync(context, relay, cancellationToken).ConfigureAwait(false);

                completedWeight += provider.Weight;
            }

            stepWatch.Stop();
            applied.Add(new AppliedStep(step.Id, step.TargetVersion, step.Description, stepWatch.Elapsed));

            journalEntry.LastCompletedStepId = step.Id;
            _journal.Write(journalEntry);
        }

        relay.StepDescription = string.Empty;
        relay.ProviderName = string.Empty;
        relay.TargetVersion = null;
    }

    private async Task<MigrationResult> RollBackAsync(
        IInstallationStrategy strategy,
        MigrationContext context,
        List<AppliedStep> applied,
        Version current,
        Version target,
        Exception? failure,
        bool canceled,
        ProgressRelay relay,
        Stopwatch stopwatch)
    {
        _logger.Log(
            canceled ? MigrationLogLevel.Warning : MigrationLogLevel.Error,
            canceled ? "The migration was cancelled; rolling back." : "The migration failed; rolling back.",
            canceled ? null : failure);

        relay.Offset = 0;
        relay.Span = 100;
        relay.Phase = MigrationPhase.RollingBack;
        relay.StepDescription = string.Empty;
        relay.ProviderName = string.Empty;

        string? survivingBackup = context.BackupDirectory;

        try
        {
            context.Items.Clear();

            MigrationJournalEntry rollingBack = new(
                context.SessionId, DateTimeOffset.UtcNow, _options.Model, context.Direction,
                current, target, context.OriginalDirectory, context.WorkingDirectory,
                context.BackupDirectory, MigrationPhase.RollingBack, applied.Count > 0 ? applied[^1].Id : null);
            _journal.Write(rollingBack);

            await strategy.RollbackAsync(context, failure, relay).ConfigureAwait(false);

            _journal.Clear();

            relay.ReportFinal(MigrationPhase.Completed, 100, canceled
                ? "Update cancelled. Your data has not been changed."
                : "Update failed. Your data has been restored.");

            return new MigrationResult(
                canceled ? MigrationOutcome.Canceled : MigrationOutcome.Failed,
                current, current, target, applied,
                context.OriginalDirectory, context.BackupDirectory, stopwatch.Elapsed,
                canceled ? null : failure, null)
            {
                CanContinue = _options.AllowRunningOnOlderData,
            };
        }
        catch (Exception rollbackError)
        {
            // The worst case, and the one the outcome enum exists to make impossible to
            // overlook. The journal is deliberately left in place so the next launch tries the
            // recovery again, and the snapshot is deliberately not cleaned up.
            _logger.Log(
                MigrationLogLevel.Critical,
                $"THE ROLLBACK FAILED. The data at '{context.WorkingDirectory}' may be inconsistent. " +
                (survivingBackup != null ? $"A pre-migration copy is at '{survivingBackup}'." : "No pre-migration copy is available."),
                rollbackError);

            relay.ReportFinal(MigrationPhase.Completed, 100, "Update failed and your data could not be restored automatically.");

            return new MigrationResult(
                MigrationOutcome.RollbackFailed, current, current, target, applied,
                context.WorkingDirectory, survivingBackup, stopwatch.Elapsed,
                canceled ? null : failure, rollbackError)
            {
                CanContinue = false,
            };
        }
    }

    private async Task RecoverIfNeededAsync(ProgressRelay relay)
    {
        MigrationJournalEntry? entry = _journal.Read();
        if (entry == null)
            return;

        relay.Offset = 0;
        relay.Span = 100;
        relay.Phase = MigrationPhase.Recovering;
        relay.ReportPhase(MigrationPhase.Recovering, 0, "Finishing an interrupted update...");

        IInstallationStrategy strategy = _strategies.TryGetValue(entry.Model, out IInstallationStrategy? match)
            ? match
            : _strategies[_options.Model];

        await strategy.RecoverAsync(entry, relay).ConfigureAwait(false);

        _journal.Clear();
        relay.ReportPhase(MigrationPhase.Recovering, 100, "Recovery complete.");
    }

    private async Task<bool> AskUserAsync(MigrationPlan plan, DataLocation location, CancellationToken cancellationToken)
    {
        long size = 0;
        try
        {
            size = DirectoryOperations.GetSize(location.Directory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // The size is a nicety for the prompt, not a precondition for it.
        }

        MigrationPromptContext promptContext = new(plan, _options.Model, _options.AllowRunningOnOlderData, size);

        try
        {
            return await _promptService!.ConfirmAsync(promptContext, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private Version ResolveCurrentVersion(DataLocation location)
    {
        if (location.Version != null)
            return location.Version;

        // Unstamped data is either a fresh install or one that predates this framework; both
        // start from InitialDataVersion, which by default replays every step.
        _logger.Log(
            MigrationLogLevel.Information,
            location.Exists
                ? $"'{location.Directory}' has no version stamp; assuming {_options.InitialDataVersion}."
                : $"No data at '{location.Directory}'; treating this as a fresh install at {_options.InitialDataVersion}.");

        return _options.InitialDataVersion;
    }

    private void TryStamp(string directory, Version version, IReadOnlyList<string> stepIds)
    {
        try
        {
            _options.DataVersionStoreFactory(directory).Write(version, stepIds);
        }
        catch (Exception ex)
        {
            _logger.Log(MigrationLogLevel.Warning, $"Could not write the version stamp in '{directory}'.", ex);
        }
    }

    private static MigrationJournalEntry WithWorkingDirectories(MigrationJournalEntry entry, MigrationContext context, MigrationPhase phase) =>
        new(
            entry.SessionId,
            entry.StartedUtc,
            entry.Model,
            entry.Direction,
            entry.FromVersion,
            entry.ToVersion,
            entry.OriginalDirectory,
            context.WorkingDirectory,
            context.BackupDirectory,
            phase,
            entry.LastCompletedStepId);

    private static MigrationResult Blocked(Exception error, Version current, Version target, string directory, TimeSpan elapsed) =>
        new(MigrationOutcome.Blocked, current, current, target, [], directory, null, elapsed, error, null)
        {
            CanContinue = false,
        };

    private static IEnumerable<IInstallationStrategy> DefaultStrategies(MigrationOptions options)
    {
        yield return new InPlaceStrategy(options, options.DataVersionStoreFactory);
        yield return new SideBySideStrategy(options, options.DataVersionStoreFactory);
    }
}
