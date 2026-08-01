// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Internal;

namespace Barbatos.Migration.Strategies;

/// <summary>
/// The <see cref="InstallationModel.SideBySideMultiFolder"/> strategy: every version owns a
/// directory under a shared root, an upgrade clones the newest existing one and migrates the
/// clone, and the previous version's directory is never opened for writing at all.
/// </summary>
/// <remarks>
/// <para>
/// This is how Visual Studio, JetBrains IDEs and most CAD packages treat their per-version
/// settings, and it buys two things the in-place model cannot. Rolling back is free - the user
/// launches the old build, which finds its own directory exactly as it left it - and a failed
/// migration cannot damage anything, because the only directory that was ever written to is
/// the staging clone, which simply gets deleted.
/// </para>
/// <para>
/// The cost is disk: two full copies of the data during the migration, and one extra copy for
/// as long as the user keeps the old version installed. That trade is why the model belongs to
/// desktop software with real data sets rather than to phone apps.
/// </para>
/// <para>
/// The migrated clone becomes the new version's directory through a single rename at commit
/// time, so no half-populated directory ever carries a version number that suggests it is
/// ready to use.
/// </para>
/// </remarks>
public sealed class SideBySideStrategy : IInstallationStrategy
{
    private const string StagingPrefix = "staging-";
    private const string DiscardPrefix = "discard-";

    private readonly MigrationOptions _options;
    private readonly Func<string, IDataVersionStore> _versionStoreFactory;

    /// <summary>Creates the strategy.</summary>
    public SideBySideStrategy(MigrationOptions options, Func<string, IDataVersionStore>? versionStoreFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _versionStoreFactory = versionStoreFactory ?? (directory => new FileDataVersionStore(directory));
    }

    /// <inheritdoc />
    public InstallationModel Model => InstallationModel.SideBySideMultiFolder;

    /// <summary>The directory this build's data will end up in, once migrated.</summary>
    public string TargetVersionDirectory =>
        Path.Combine(PathGuard.Normalize(_options.DataDirectory), _options.VersionDirectoryName(_options.TargetDataVersion));

    /// <inheritdoc />
    public DataLocation ResolveCurrentData()
    {
        string root = PathGuard.Normalize(_options.DataDirectory);
        string targetDirectory = Path.Combine(root, _options.VersionDirectoryName(_options.TargetDataVersion));

        // This build's own directory already exists: nothing to clone, and its stamp decides
        // whether any steps are still outstanding within that version.
        if (DirectoryOperations.HasContent(targetDirectory))
            return new DataLocation(targetDirectory, _versionStoreFactory(targetDirectory).Read(), exists: true);

        KeyValuePair<Version, string>? source = FindNewestInstalledVersion(root, _options.TargetDataVersion);
        if (source == null)
        {
            // Nothing installed at all: a fresh install, which materialises directly into the
            // target directory rather than being cloned from anywhere.
            return new DataLocation(targetDirectory, version: null, exists: false);
        }

        string sourceDirectory = source.Value.Value;

        // Prefer the stamp inside the source directory; fall back to the version its folder
        // name encodes, which is all a directory written by an older build will have.
        Version version = _versionStoreFactory(sourceDirectory).Read() ?? source.Value.Key;

        return new DataLocation(sourceDirectory, version, exists: true);
    }

    /// <inheritdoc />
    public bool RequiresRunWithEmptyPlan(DataLocation currentData) =>
        !PathGuard.AreSame(currentData.Directory, TargetVersionDirectory) || !currentData.Exists;

    /// <inheritdoc />
    public Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(PathGuard.Normalize(_options.BackupRootDirectory), StagingPrefix + context.SessionId);

        // Pointed at the staging clone before anything that can throw, because the rollback
        // deletes whatever WorkingDirectory names. It starts out as the previous version's
        // directory, so a preparation that failed before this line would have the cleanup
        // delete the very data this model exists to leave alone.
        context.SetWorkingDirectory(staging);

        // No snapshot: the source directory is left untouched for the whole run, so it *is*
        // the backup. Reporting null here is what tells the engine's rollback path that there
        // is nothing to restore.
        context.SetBackupDirectory(null);

        string backupRoot = DirectoryOperations.Ensure(_options.BackupRootDirectory);

        DirectoryOperations.Delete(staging);
        Directory.CreateDirectory(staging);

        string source = PathGuard.Normalize(context.OriginalDirectory);
        if (!DirectoryOperations.HasContent(source) || PathGuard.AreSame(source, staging))
        {
            context.Logger.Log(MigrationLogLevel.Information, $"No previous version to clone; starting {context.TargetDataVersion} from an empty directory.");
            return Task.CompletedTask;
        }

        long size = DirectoryOperations.GetSize(source, cancellationToken);
        StrategySupport.EnsureEnoughFreeSpace(_options, size, backupRoot);

        context.Logger.Log(
            MigrationLogLevel.Information,
            $"Cloning {StrategySupport.FormatSize(size)} from '{source}' into '{staging}' for version {context.TargetDataVersion}.");

        StrategySupport.CopyWithProgress(source, staging, size, MigrationPhase.Preparing, "Copying your data to the new version...", progress, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress)
    {
        StrategySupport.Report(progress, MigrationPhase.Committing, 100, "Finalising...");

        _versionStoreFactory(context.WorkingDirectory).Write(context.TargetDataVersion, appliedStepIds);

        string finalDirectory = TargetVersionDirectory;
        string discard = Path.Combine(_options.BackupRootDirectory, DiscardPrefix + context.SessionId);

        // One rename publishes the whole thing. Until this line runs, the target version has no
        // directory at all - which is exactly what a version that is not ready yet should look
        // like to the next launch.
        DirectoryOperations.Replace(finalDirectory, context.WorkingDirectory, discard);

        context.SetWorkingDirectory(finalDirectory);
        context.Logger.Log(MigrationLogLevel.Information, $"Published version {context.TargetDataVersion} at '{finalDirectory}'. '{context.OriginalDirectory}' was left untouched.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress)
    {
        StrategySupport.Report(progress, MigrationPhase.RollingBack, 0, "Cleaning up...");

        // The entire rollback. The previous version's directory was never written to, so
        // "restoring" it is a no-op and the user can go straight back to the old build.
        //
        // The guard states that invariant rather than assuming it: this model never writes to
        // the directory it cloned from, so there is no sequence of events in which deleting it
        // is the right thing to do, and a cleanup that reached for it would be destroying the
        // user's only copy.
        if (!PathGuard.AreSame(context.WorkingDirectory, context.OriginalDirectory))
        {
            DirectoryOperations.TryDelete(context.WorkingDirectory, context.Logger);
        }
        else
        {
            context.Logger.Log(
                MigrationLogLevel.Information,
                $"Nothing to clean up - the run failed before it had a staging clone, so '{context.OriginalDirectory}' was never touched.");
        }

        context.Logger.Log(
            MigrationLogLevel.Information,
            error is OperationCanceledException
                ? $"Cancelled; discarded the staging clone. '{context.OriginalDirectory}' is unchanged."
                : $"Failed; discarded the staging clone. '{context.OriginalDirectory}' is unchanged.");

        StrategySupport.Report(progress, MigrationPhase.RollingBack, 100, "Your existing version is unchanged.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress)
    {
        _options.Logger.Log(
            MigrationLogLevel.Warning,
            $"A migration to {journal.ToVersion} started at {journal.StartedUtc:u} did not finish (phase: {journal.Phase}). Discarding its staging clone.");

        string backupRoot = _options.BackupRootDirectory;
        string discard = Path.Combine(backupRoot, DiscardPrefix + journal.SessionId);

        // Killed mid-commit, after the target directory had been renamed aside but before the
        // clone took its place: put the original back, then discard the clone as usual.
        if (Directory.Exists(discard) && !Directory.Exists(journal.WorkingDirectory))
        {
            string finalDirectory = TargetVersionDirectory;
            if (!Directory.Exists(finalDirectory))
            {
                _options.Logger.Log(MigrationLogLevel.Warning, "A commit was interrupted mid-swap; restoring the renamed directory.");
                DirectoryOperations.Move(discard, finalDirectory);
            }
        }

        DirectoryOperations.TryDelete(Path.Combine(backupRoot, StagingPrefix + journal.SessionId), _options.Logger);
        DirectoryOperations.TryDelete(journal.WorkingDirectory, _options.Logger);
        DirectoryOperations.TryDelete(discard, _options.Logger);

        // Sweep up clones abandoned by runs whose journal was lost as well - otherwise a
        // repeatedly-crashing upgrade quietly fills the disk with staging copies.
        if (Directory.Exists(backupRoot))
        {
            foreach (string orphan in Directory.EnumerateDirectories(backupRoot, StagingPrefix + "*"))
                DirectoryOperations.TryDelete(orphan, _options.Logger);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The newest installed version directory at or below <paramref name="upperBound"/>, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// A single pass keeping the running maximum, rather than collecting every candidate and
    /// sorting: only the largest is ever wanted, and the version-directory check is the cheap
    /// part next to the <see cref="DirectoryOperations.HasContent"/> probe it guards.
    /// </remarks>
    private static KeyValuePair<Version, string>? FindNewestInstalledVersion(string root, Version upperBound)
    {
        if (!Directory.Exists(root))
            return null;

        Version? newest = null;
        string? newestDirectory = null;

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);

            // Skip the engine's own working area and anything else that is not a version.
            if (name.Length == 0 || name[0] == '.')
                continue;
            if (!Version.TryParse(name, out Version? version))
                continue;
            if (version > upperBound)
                continue;
            if (newest != null && version <= newest)
                continue;
            if (!DirectoryOperations.HasContent(directory))
                continue;

            newest = version;
            newestDirectory = directory;
        }

        return newest == null ? null : new KeyValuePair<Version, string>(newest, newestDirectory!);
    }
}
