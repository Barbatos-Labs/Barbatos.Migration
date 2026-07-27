// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// Where the current data is and what version it claims to be.
/// </summary>
public sealed class DataLocation
{
    /// <summary>Creates a location.</summary>
    public DataLocation(string directory, Version? version, bool exists)
    {
        Directory = directory;
        Version = version;
        Exists = exists;
    }

    /// <summary>The directory holding the current data.</summary>
    public string Directory { get; }

    /// <summary>
    /// The stamped data version, or <see langword="null"/> when the data has never been stamped
    /// - a fresh install, or one that predates this framework.
    /// </summary>
    public Version? Version { get; }

    /// <summary>Whether the directory exists and holds anything.</summary>
    public bool Exists { get; }
}

/// <summary>
/// Everything that differs between the two installation models: where the data is, how it is
/// protected while it is being rewritten, and what "undo" means.
/// </summary>
/// <remarks>
/// The engine drives every run through exactly one strategy and never touches the file system
/// itself, so adding a third model later - a database-only install, say, or one that streams a
/// snapshot to cloud storage - means writing one class rather than editing the engine.
/// </remarks>
public interface IInstallationStrategy
{
    /// <summary>The model this strategy implements.</summary>
    InstallationModel Model { get; }

    /// <summary>
    /// Finds the current data and its version. Read-only: called during planning, before the
    /// user has been asked anything.
    /// </summary>
    DataLocation ResolveCurrentData();

    /// <summary>
    /// Whether the strategy still has work to do when the plan is empty.
    /// </summary>
    /// <remarks>
    /// In-place answers <see langword="false"/>: no steps means nothing to change. Side-by-side
    /// answers <see langword="true"/> whenever this build's directory does not exist yet, since
    /// the previous version's data still has to be cloned into it even if no step transforms
    /// it - shipping 2.0 with no schema change must not leave 2.0 with no data.
    /// </remarks>
    bool RequiresRunWithEmptyPlan(DataLocation currentData);

    /// <summary>
    /// Gets the data ready to be migrated: snapshot it (in-place) or clone it (side-by-side),
    /// then point <see cref="MigrationContext.WorkingDirectory"/> at whatever the providers
    /// should write to. Cancellable, and the longest phase for large data sets.
    /// </summary>
    Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the migrated data and stamps its new version. Not cancellable - by this point
    /// abandoning the run would cost more than finishing it.
    /// </summary>
    Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress);

    /// <summary>
    /// Returns the data to exactly the state it was in before <see cref="PrepareAsync"/> ran.
    /// Not cancellable. Throwing from here produces
    /// <see cref="MigrationOutcome.RollbackFailed"/>, so implementations must leave the
    /// snapshot on disk rather than clean up after themselves when they fail.
    /// </summary>
    Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress);

    /// <summary>
    /// Cleans up after a run that was killed before it could finish, using the journal entry it
    /// left behind. Runs at startup, before any new migration is planned.
    /// </summary>
    Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress);
}
