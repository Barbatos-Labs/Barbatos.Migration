// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.Migration.Internal;

namespace Barbatos.Migration.Strategies;

/// <summary>
/// The <see cref="InstallationModel.InPlaceSingleFolder"/> strategy: take a full snapshot of the
/// data directory, let the providers rewrite the real thing, and swap the snapshot back if
/// anything goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// The restore is the part worth reading closely. The obvious implementation - delete the data
/// directory, then copy the snapshot back into place - has a window, lasting the whole length
/// of the copy, in which the user has neither their old data nor their new data. On a
/// multi-gigabyte data set that window is minutes long, and a crash inside it is unrecoverable.
/// </para>
/// <para>
/// Instead the restore is three renames (see <see cref="DirectoryOperations.Replace"/>): the
/// half-migrated directory is renamed aside, the snapshot is renamed into its place, and only
/// then is the discarded copy deleted. Renames within a volume are atomic, so at every instant
/// there is a complete copy of the data under a name the next launch can find.
/// </para>
/// </remarks>
public sealed class InPlaceStrategy : IInstallationStrategy
{
    private const string SnapshotPrefix = "snapshot-";
    private const string DiscardPrefix = "discard-";
    private const string RetainedPrefix = "backup-";

    private readonly MigrationOptions _options;
    private readonly Func<string, IDataVersionStore> _versionStoreFactory;

    /// <summary>Creates the strategy.</summary>
    public InPlaceStrategy(MigrationOptions options, Func<string, IDataVersionStore>? versionStoreFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _versionStoreFactory = versionStoreFactory ?? (directory => new FileDataVersionStore(directory));
    }

    /// <inheritdoc />
    public InstallationModel Model => InstallationModel.InPlaceSingleFolder;

    /// <inheritdoc />
    public DataLocation ResolveCurrentData()
    {
        string directory = PathGuard.Normalize(_options.DataDirectory);
        bool exists = DirectoryOperations.HasContent(directory);
        Version? version = Directory.Exists(directory) ? _versionStoreFactory(directory).Read() : null;

        return new DataLocation(directory, version, exists);
    }

    /// <inheritdoc />
    public bool RequiresRunWithEmptyPlan(DataLocation currentData) => false;

    /// <inheritdoc />
    public Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        string data = PathGuard.Normalize(context.OriginalDirectory);
        string backupRoot = DirectoryOperations.Ensure(_options.BackupRootDirectory);
        string snapshot = Path.Combine(backupRoot, SnapshotPrefix + context.SessionId);

        context.SetWorkingDirectory(data);

        // Deliberately left null until the copy has finished. BackupDirectory is what the
        // engine's rollback restores from, and a half-written snapshot is not a copy of the
        // data - it is a fragment of it. Announcing it before it is complete means an
        // interrupted copy (the user pressing Cancel, the disk filling, a scanner locking one
        // file) is followed by a "rollback" that swaps that fragment over a data directory no
        // provider has touched yet, destroying the very data the snapshot was being taken to
        // protect. Nothing has been modified at this point, so the correct response to a failed
        // preparation is to restore nothing at all.
        context.SetBackupDirectory(null);

        Directory.CreateDirectory(data);
        DirectoryOperations.Delete(snapshot);

        long size = DirectoryOperations.GetSize(data, cancellationToken);
        StrategySupport.EnsureEnoughFreeSpace(_options, size, backupRoot);

        context.Logger.Log(
            MigrationLogLevel.Information,
            $"Snapshotting {StrategySupport.FormatSize(size)} from '{data}' to '{snapshot}'.");

        try
        {
            StrategySupport.CopyWithProgress(data, snapshot, size, MigrationPhase.Preparing, "Backing up your data...", progress, cancellationToken);
        }
        catch
        {
            // The fragment is of no use to anyone and would otherwise sit in the backup root
            // until something else happened to clear it.
            DirectoryOperations.TryDelete(snapshot, context.Logger);
            throw;
        }

        // Complete, and only now safe to restore from.
        context.SetBackupDirectory(snapshot);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress)
    {
        StrategySupport.Report(progress, MigrationPhase.Committing, 100, "Finalising...");

        _versionStoreFactory(context.WorkingDirectory).Write(context.TargetDataVersion, appliedStepIds);

        string? snapshot = context.BackupDirectory;
        if (snapshot == null || !Directory.Exists(snapshot))
            return Task.CompletedTask;

        if (_options.BackupRetentionCount <= 0)
        {
            DirectoryOperations.TryDelete(snapshot, context.Logger);
            context.SetBackupDirectory(null);
            return Task.CompletedTask;
        }

        // Renaming the snapshot out of the "in-flight" namespace is what promotes it from
        // "restore this if we crash" to "the user's safety net for the next few days".
        //
        // UTC, not local time: PruneRetainedBackups keeps the newest N by sorting these names,
        // which only tracks the real order if the timestamps advance monotonically. Local time
        // does not - it goes backwards by an hour every autumn, and a backup taken in that hour
        // would sort as older than one taken before it and be pruned first, so the copy the user
        // is most likely to want back is the one that gets deleted.
        string retained = Path.Combine(
            _options.BackupRootDirectory,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1:yyyyMMdd-HHmmss}-v{2}",
                RetainedPrefix,
                DateTime.UtcNow,
                context.CurrentDataVersion));

        try
        {
            DirectoryOperations.Delete(retained);
            DirectoryOperations.Move(snapshot, retained);
            context.SetBackupDirectory(retained);
            context.Logger.Log(MigrationLogLevel.Information, $"Kept a pre-migration backup at '{retained}'.");
        }
        catch (Exception ex)
        {
            context.Logger.Log(MigrationLogLevel.Warning, $"Could not retain the snapshot at '{retained}'.", ex);
        }

        PruneRetainedBackups(context.Logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress)
    {
        string? snapshot = context.BackupDirectory;
        if (snapshot == null || !Directory.Exists(snapshot))
        {
            // Nothing was snapshotted, which means nothing was migrated either: Prepare either
            // never ran or failed before it produced anything.
            context.Logger.Log(MigrationLogLevel.Information, "Nothing to roll back - no snapshot was taken.");
            return Task.CompletedTask;
        }

        StrategySupport.Report(progress, MigrationPhase.RollingBack, 0, "Restoring your data...");
        context.Logger.Log(MigrationLogLevel.Warning, $"Restoring '{context.WorkingDirectory}' from '{snapshot}'.", error);

        string discard = Path.Combine(_options.BackupRootDirectory, DiscardPrefix + context.SessionId);
        DirectoryOperations.Replace(context.WorkingDirectory, snapshot, discard);

        // The snapshot has been renamed into place, so it is no longer a separate copy - and
        // the data directory is byte-for-byte what it was before the run.
        context.SetBackupDirectory(null);

        StrategySupport.Report(progress, MigrationPhase.RollingBack, 100, "Your data has been restored.");
        context.Logger.Log(MigrationLogLevel.Information, "Rollback complete; data is back at its pre-migration state.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress)
    {
        string backupRoot = DirectoryOperations.Ensure(_options.BackupRootDirectory);
        string snapshot = journal.BackupDirectory ?? Path.Combine(backupRoot, SnapshotPrefix + journal.SessionId);
        string discard = Path.Combine(backupRoot, DiscardPrefix + journal.SessionId);

        _options.Logger.Log(
            MigrationLogLevel.Warning,
            $"A migration to {journal.ToVersion} started at {journal.StartedUtc:u} did not finish (phase: {journal.Phase}). Recovering.");

        // Killed during Prepare: the snapshot is incomplete, but so is the reason to use it -
        // no provider had run yet, so the data directory is still untouched and correct.
        if (journal.Phase == MigrationPhase.Preparing)
        {
            DirectoryOperations.TryDelete(snapshot, _options.Logger);
            DirectoryOperations.TryDelete(discard, _options.Logger);
            _options.Logger.Log(MigrationLogLevel.Information, "The run was killed before any data changed; discarded the partial snapshot.");
            return Task.CompletedTask;
        }

        // Killed mid-restore: the data directory may be missing entirely because it had been
        // renamed to the discard path. Putting that back first turns this into the ordinary
        // case below.
        if (!Directory.Exists(journal.WorkingDirectory) && Directory.Exists(discard))
        {
            _options.Logger.Log(MigrationLogLevel.Warning, "A rollback was interrupted mid-swap; restoring the renamed directory.");
            DirectoryOperations.Move(discard, journal.WorkingDirectory);
        }

        if (Directory.Exists(snapshot))
        {
            StrategySupport.Report(progress, MigrationPhase.Recovering, 0, "Restoring your data after an interrupted update...");
            DirectoryOperations.Replace(journal.WorkingDirectory, snapshot, discard);
            StrategySupport.Report(progress, MigrationPhase.Recovering, 100, "Your data has been restored.");
            _options.Logger.Log(MigrationLogLevel.Information, $"Recovered '{journal.WorkingDirectory}' back to {journal.FromVersion}.");
        }
        else
        {
            _options.Logger.Log(
                MigrationLogLevel.Warning,
                $"No snapshot survives at '{snapshot}'; leaving the data directory as it is and letting the version stamp decide what runs next.");
        }

        DirectoryOperations.TryDelete(discard, _options.Logger);
        return Task.CompletedTask;
    }

    private void PruneRetainedBackups(IMigrationLogger logger)
    {
        try
        {
            List<string> retained = Directory
                .EnumerateDirectories(_options.BackupRootDirectory, RetainedPrefix + "*")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .ToList();

            for (int i = _options.BackupRetentionCount; i < retained.Count; i++)
                DirectoryOperations.TryDelete(retained[i], logger);
        }
        catch (Exception ex)
        {
            logger.Log(MigrationLogLevel.Warning, "Could not prune old backups.", ex);
        }
    }

}
