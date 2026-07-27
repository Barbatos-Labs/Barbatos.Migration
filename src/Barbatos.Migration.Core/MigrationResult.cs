// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// How a migration run ended. Callers branch on this rather than on a <see langword="bool"/>,
/// because "the app may keep running" and "the data changed" are different questions and a
/// single flag cannot answer both - see <see cref="MigrationResult.CanContinue"/>.
/// </summary>
public enum MigrationOutcome
{
    /// <summary>Data was already at the target version. Nothing ran, nothing changed.</summary>
    UpToDate,

    /// <summary>Every step completed and the new data version was committed.</summary>
    Succeeded,

    /// <summary>
    /// The user cancelled. Data has been restored to exactly the state it was in before the
    /// run started.
    /// </summary>
    Canceled,

    /// <summary>
    /// The user declined or postponed the migration in
    /// <see cref="UpdateTriggerMode.ManualInteractive"/>. Nothing ran and nothing changed.
    /// </summary>
    Deferred,

    /// <summary>
    /// A step failed. Data has been restored to exactly the state it was in before the run
    /// started; see <see cref="MigrationResult.Error"/> for why.
    /// </summary>
    Failed,

    /// <summary>
    /// A step failed <em>and</em> the rollback that followed also failed. This is the one
    /// outcome where user data may be inconsistent: surface it loudly, point the user at
    /// <see cref="MigrationResult.BackupDirectory"/>, and do not start the app normally.
    /// </summary>
    RollbackFailed,

    /// <summary>
    /// The migration could not even be attempted - another process holds the migration lock,
    /// the data is newer than this build and cannot be downgraded, a required step is missing,
    /// or there is not enough free disk space for the snapshot. See
    /// <see cref="MigrationResult.Error"/>.
    /// </summary>
    Blocked,
}

/// <summary>
/// The outcome of a single <see cref="MigrationEngine.RunAsync"/> call.
/// </summary>
public sealed class MigrationResult
{
    internal MigrationResult(
        MigrationOutcome outcome,
        Version fromVersion,
        Version currentVersion,
        Version targetVersion,
        IReadOnlyList<AppliedStep> appliedSteps,
        string workingDirectory,
        string? backupDirectory,
        TimeSpan duration,
        Exception? error,
        Exception? rollbackError)
    {
        Outcome = outcome;
        FromVersion = fromVersion;
        CurrentVersion = currentVersion;
        TargetVersion = targetVersion;
        AppliedSteps = appliedSteps;
        WorkingDirectory = workingDirectory;
        BackupDirectory = backupDirectory;
        Duration = duration;
        Error = error;
        RollbackError = rollbackError;
    }

    /// <summary>How the run ended.</summary>
    public MigrationOutcome Outcome { get; }

    /// <summary>
    /// <see langword="true"/> when the data reached <see cref="TargetVersion"/>.
    /// </summary>
    public bool IsSuccess =>
        Outcome is MigrationOutcome.Succeeded or MigrationOutcome.UpToDate;

    /// <summary>
    /// <see langword="true"/> when it is safe to carry on into the application.
    /// </summary>
    /// <remarks>
    /// This is deliberately <em>not</em> the same as <see cref="IsSuccess"/>. After a clean
    /// rollback the data is intact and self-consistent, but it is still at the old version -
    /// whether the app can run against it depends on the app, so
    /// <see cref="MigrationOptions.AllowRunningOnOlderData"/> decides. After
    /// <see cref="MigrationOutcome.RollbackFailed"/> the answer is always no.
    /// </remarks>
    public bool CanContinue { get; internal set; }

    /// <summary>The data version the run started from.</summary>
    public Version FromVersion { get; }

    /// <summary>
    /// The data version in effect now the run has finished. Equal to
    /// <see cref="TargetVersion"/> on success and to <see cref="FromVersion"/> after a
    /// cancellation, deferral or clean rollback.
    /// </summary>
    public Version CurrentVersion { get; }

    /// <summary>The data version the run was aiming for.</summary>
    public Version TargetVersion { get; }

    /// <summary>The steps that actually ran, in the order they ran.</summary>
    public IReadOnlyList<AppliedStep> AppliedSteps { get; }

    /// <summary>
    /// The directory the application should read its data from now. Under
    /// <see cref="InstallationModel.SideBySideMultiFolder"/> a successful upgrade moves this to
    /// the new version's folder, so always honour it instead of recomputing the path.
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// Where the pre-migration snapshot lives, when one was kept - either because
    /// <see cref="MigrationOptions.BackupRetentionCount"/> asked for it or because the rollback
    /// failed and the snapshot is now the user's only intact copy.
    /// </summary>
    public string? BackupDirectory { get; }

    /// <summary>How long the whole run took, including preparation and rollback.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Why the run failed, when it did.</summary>
    public Exception? Error { get; }

    /// <summary>
    /// Why the rollback failed, when <see cref="Outcome"/> is
    /// <see cref="MigrationOutcome.RollbackFailed"/>.
    /// </summary>
    public Exception? RollbackError { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Outcome}: {FormatVersion(FromVersion)} -> {FormatVersion(CurrentVersion)} " +
        $"({AppliedSteps.Count} step(s), {Duration.TotalSeconds:F1}s)";

    private static string FormatVersion(Version version) => version.ToString();
}

/// <summary>
/// A record of one step that ran during a migration.
/// </summary>
public sealed class AppliedStep
{
    internal AppliedStep(string id, Version targetVersion, string description, TimeSpan duration)
    {
        Id = id;
        TargetVersion = targetVersion;
        Description = description;
        Duration = duration;
    }

    /// <summary><see cref="IMigrationStep.Id"/> of the step.</summary>
    public string Id { get; }

    /// <summary>The version the step migrated to (or, for a downgrade, away from).</summary>
    public Version TargetVersion { get; }

    /// <summary><see cref="IMigrationStep.Description"/> of the step.</summary>
    public string Description { get; }

    /// <summary>How long the step took.</summary>
    public TimeSpan Duration { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TargetVersion} {Description} ({Duration.TotalMilliseconds:F0}ms)";
}
