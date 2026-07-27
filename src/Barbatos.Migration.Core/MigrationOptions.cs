// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.Migration.Internal;

namespace Barbatos.Migration;

/// <summary>
/// How the engine should behave for one application.
/// </summary>
public sealed class MigrationOptions
{
    /// <summary>The configuration section this binds to when configuration is available.</summary>
    public const string SectionName = "Barbatos:Migration";

    private string _dataDirectory = string.Empty;
    private string? _backupRootDirectory;

    /// <summary>
    /// Where the data lives.
    /// </summary>
    /// <remarks>
    /// For <see cref="InstallationModel.InPlaceSingleFolder"/> this is the data directory
    /// itself. For <see cref="InstallationModel.SideBySideMultiFolder"/> it is the
    /// <em>parent</em> that holds the per-version directories, and the engine picks the right
    /// one inside it. On WPF this is filled in from
    /// <c>Barbatos.Wpf.Storage.IFileSystem.AppDataDirectory</c>.
    /// </remarks>
    public string DataDirectory
    {
        get => _dataDirectory;
        set => _dataDirectory = value ?? string.Empty;
    }

    /// <summary>
    /// Where snapshots, the journal and the lock file live. Defaults to a <c>.migration</c>
    /// directory <em>beside</em> <see cref="DataDirectory"/> - never inside it, so that
    /// replacing the data directory wholesale does not take the journal with it.
    /// </summary>
    public string BackupRootDirectory
    {
        get => _backupRootDirectory ?? GetDefaultBackupRoot();
        set => _backupRootDirectory = value;
    }

    /// <summary>How this application lays its versions out. Defaults to in-place.</summary>
    public InstallationModel Model { get; set; } = InstallationModel.InPlaceSingleFolder;

    /// <summary>
    /// The data version the application needs. Usually derived from the app version - on WPF
    /// the hosting package defaults it to <c>AppInfo.Version</c>.
    /// </summary>
    public Version TargetDataVersion { get; set; } = new Version(1, 0, 0, 0);

    /// <summary>
    /// The version to assume for data that has never been stamped. Defaults to
    /// <c>0.0.0.0</c>, so a fresh install runs every registered step from the beginning and
    /// ends up identical to an installation that has been upgraded all the way through.
    /// </summary>
    /// <remarks>
    /// Set this to <see cref="TargetDataVersion"/> instead if a fresh install already creates
    /// its schema at the current shape and the steps would only be re-doing that work. The
    /// hosting packages can also detect a genuinely first-ever launch through
    /// <c>IVersionTracking.IsFirstLaunchEver</c>, which is more reliable than guessing from an
    /// empty directory.
    /// </remarks>
    public Version InitialDataVersion { get; set; } = new Version(0, 0, 0, 0);

    /// <summary>
    /// How the user is asked about a pending migration. Defaults to
    /// <see cref="UpdateTriggerMode.SilentAutoUpdate"/>.
    /// </summary>
    public UpdateTriggerMode TriggerMode { get; set; } = UpdateTriggerMode.SilentAutoUpdate;

    /// <summary>
    /// How many successful-migration snapshots to keep. <c>0</c> deletes the snapshot as soon
    /// as the migration commits; the default of <c>1</c> keeps the most recent one, which is
    /// what lets a user who only notices the damage tomorrow still get their data back.
    /// Snapshots left behind by a <em>failed</em> rollback are never pruned.
    /// </summary>
    public int BackupRetentionCount { get; set; } = 1;

    /// <summary>
    /// How much free space the snapshot must have, as a multiple of the data size. The default
    /// <c>1.2</c> leaves headroom for the migration itself to grow the data. Checked before the
    /// snapshot starts, because running out of disk halfway through a copy is a failure mode
    /// worth converting into a clean up-front error message.
    /// </summary>
    public double RequiredFreeSpaceFactor { get; set; } = 1.2;

    /// <summary>
    /// Skips the free-space check. Useful on network shares and container volumes where the
    /// reported figure is meaningless.
    /// </summary>
    public bool SkipFreeSpaceCheck { get; set; }

    /// <summary>
    /// Whether the application can still run when a migration was cancelled, deferred or rolled
    /// back and the data is therefore still at the old version. Defaults to
    /// <see langword="false"/>, because a new binary reading an old schema is how silent data
    /// corruption starts; it drives <see cref="MigrationResult.CanContinue"/> only, and the
    /// application decides what to do about it.
    /// </summary>
    public bool AllowRunningOnOlderData { get; set; }

    /// <summary>
    /// Whether data that is <em>newer</em> than this build may be downgraded to match, rather
    /// than reported as <see cref="MigrationOutcome.Blocked"/>. Only meaningful for
    /// <see cref="InstallationModel.InPlaceSingleFolder"/>, and only possible when every step
    /// in between implements <see cref="IMigrationProvider.DownAsync"/>. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool AllowDowngrade { get; set; }

    /// <summary>
    /// Names the per-version directories under <see cref="InstallationModel.SideBySideMultiFolder"/>.
    /// Defaults to the three-part version (<c>2.1.0</c>). The engine parses these names back
    /// into versions to find the newest installed one, so whatever this produces must round-trip
    /// through <see cref="Version.TryParse(string, out Version)"/>.
    /// </summary>
    public Func<Version, string> VersionDirectoryName { get; set; } = DefaultVersionDirectoryName;

    /// <summary>
    /// Creates the <see cref="IDataVersionStore"/> for a given data directory. Defaults to
    /// <see cref="FileDataVersionStore"/>. Replace it if the version belongs somewhere else -
    /// SQLite's <c>PRAGMA user_version</c>, say, or a row in a settings table.
    /// </summary>
    public Func<string, IDataVersionStore> DataVersionStoreFactory { get; set; } =
        directory => new FileDataVersionStore(directory);

    /// <summary>Where the engine logs. Defaults to discarding everything.</summary>
    public IMigrationLogger Logger { get; set; } = NullMigrationLogger.Instance;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            throw new MigrationException(
                "MigrationOptions.DataDirectory has not been set. It must point at the application's data directory " +
                "(on WPF, IFileSystem.AppDataDirectory).");
        }

        if (TargetDataVersion == null)
            throw new MigrationException("MigrationOptions.TargetDataVersion is required.");
        if (InitialDataVersion == null)
            throw new MigrationException("MigrationOptions.InitialDataVersion is required.");
        if (VersionDirectoryName == null)
            throw new MigrationException("MigrationOptions.VersionDirectoryName is required.");
        if (DataVersionStoreFactory == null)
            throw new MigrationException("MigrationOptions.DataVersionStoreFactory is required.");
        if (BackupRetentionCount < 0)
            throw new MigrationException("MigrationOptions.BackupRetentionCount cannot be negative.");
        if (RequiredFreeSpaceFactor < 1.0)
            throw new MigrationException("MigrationOptions.RequiredFreeSpaceFactor must be at least 1.0.");

        PathGuard.EnsureSafeToDelete(DataDirectory, nameof(DataDirectory));
        PathGuard.EnsureSafeToDelete(BackupRootDirectory, nameof(BackupRootDirectory));

        if (Model == InstallationModel.InPlaceSingleFolder)
            PathGuard.EnsureDisjoint(DataDirectory, BackupRootDirectory);
    }

    private static string DefaultVersionDirectoryName(Version version) =>
        string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", version.Major, version.Minor, Math.Max(0, version.Build));

    private string GetDefaultBackupRoot()
    {
        string data = PathGuard.Normalize(DataDirectory);

        // Side-by-side keeps its staging area under the version root, which is a parent of the
        // per-version directories rather than a data directory itself - so ".migration" there
        // is already disjoint from every version's data.
        if (Model == InstallationModel.SideBySideMultiFolder)
            return Path.Combine(data, ".migration");

        string? parent = Path.GetDirectoryName(data);
        if (string.IsNullOrEmpty(parent))
        {
            throw new MigrationException(
                $"Cannot derive a backup directory beside '{data}' because it has no parent. " +
                "Set MigrationOptions.BackupRootDirectory explicitly.");
        }

        return Path.Combine(parent!, ".migration");
    }
}

/// <summary>
/// How the user finds out that their data is about to be migrated.
/// </summary>
/// <remarks>
/// This is about the <em>data</em> migration, not about downloading a new build. Deferring a
/// download is always safe; deferring a data migration is only safe if the application can
/// actually run against the old data, which is what
/// <see cref="MigrationOptions.AllowRunningOnOlderData"/> declares. Set
/// <see cref="ManualInteractive"/> when a migration is long enough that a user in the middle of
/// a working day deserves to choose when it happens.
/// </remarks>
public enum UpdateTriggerMode
{
    /// <summary>
    /// Migrate as soon as the application starts, showing progress but not asking. Right for
    /// phone apps, SaaS clients and anything where the migration takes a second or two.
    /// </summary>
    SilentAutoUpdate,

    /// <summary>
    /// Ask first, through <see cref="IUpdatePromptService"/>, showing what will change and how
    /// long it may take. Right for CAD, industrial and other applications where a user may have
    /// opened the app specifically to finish something urgent.
    /// </summary>
    ManualInteractive,
}
