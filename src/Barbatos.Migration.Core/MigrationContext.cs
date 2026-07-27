// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.


namespace Barbatos.Migration;

/// <summary>
/// The engine's <see cref="IMigrationContext"/> implementation. Created by
/// <see cref="MigrationEngine"/> for each run; the installation strategy is what moves
/// <see cref="WorkingDirectory"/> when it needs to.
/// </summary>
public sealed class MigrationContext : IMigrationContext
{
    internal MigrationContext(
        string sessionId,
        Version currentDataVersion,
        Version targetDataVersion,
        MigrationDirection direction,
        InstallationModel model,
        string originalDirectory,
        IMigrationLogger logger)
    {
        SessionId = sessionId;
        CurrentDataVersion = currentDataVersion;
        TargetDataVersion = targetDataVersion;
        Direction = direction;
        Model = model;
        OriginalDirectory = originalDirectory;
        WorkingDirectory = originalDirectory;
        Logger = logger;
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Identifies this run. Strategies use it to name their temporary directories, so two runs
    /// - or a new run and the leftovers of one that was killed - never collide.
    /// </summary>
    public string SessionId { get; }

    /// <inheritdoc />
    public Version CurrentDataVersion { get; }

    /// <inheritdoc />
    public Version TargetDataVersion { get; }

    /// <inheritdoc />
    public MigrationDirection Direction { get; }

    /// <inheritdoc />
    public InstallationModel Model { get; }

    /// <inheritdoc />
    public string WorkingDirectory { get; private set; }

    /// <inheritdoc />
    public string OriginalDirectory { get; }

    /// <inheritdoc />
    public string? BackupDirectory { get; private set; }

    /// <inheritdoc />
    public IMigrationLogger Logger { get; }

    /// <inheritdoc />
    public IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Points the run at the directory the providers should write to.
    /// </summary>
    /// <remarks>
    /// <b>For <see cref="IInstallationStrategy"/> implementations only</b>, and normally only
    /// from <see cref="IInstallationStrategy.PrepareAsync"/> and
    /// <see cref="IInstallationStrategy.CommitAsync"/> - the side-by-side strategy points this
    /// at its staging clone while migrating and at the published version directory afterwards.
    /// A provider that calls it is redirecting every provider after it, which is never what was
    /// meant. It is a method rather than a settable property so that misuse is conspicuous.
    /// </remarks>
    /// <param name="directory">The directory providers must operate on.</param>
    public void SetWorkingDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        WorkingDirectory = directory;
    }

    /// <summary>
    /// Records where the pre-migration snapshot lives, or clears it once the snapshot has been
    /// consumed or was never taken.
    /// </summary>
    /// <remarks>
    /// <b>For <see cref="IInstallationStrategy"/> implementations only.</b> The engine reads it
    /// to fill in <see cref="MigrationResult.BackupDirectory"/> and to decide whether there is
    /// anything to restore, so a strategy that takes no snapshot - side-by-side, where the
    /// untouched original <em>is</em> the backup - must leave it <see langword="null"/>.
    /// </remarks>
    /// <param name="directory">The snapshot directory, or <see langword="null"/> when there is none.</param>
    public void SetBackupDirectory(string? directory) => BackupDirectory = directory;

    /// <inheritdoc />
    public string GetWorkingPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return Path.Combine(WorkingDirectory, relativePath);
    }
}
