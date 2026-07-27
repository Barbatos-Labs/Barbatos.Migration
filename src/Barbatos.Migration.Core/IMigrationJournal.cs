// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.Migration.Internal;

namespace Barbatos.Migration;

/// <summary>
/// The record of a migration that is currently in flight.
/// </summary>
/// <remarks>
/// A <see langword="try"/>/<see langword="catch"/> only protects against exceptions. It does
/// nothing about the user killing the process from Task Manager, the battery running out, or
/// Windows deciding to restart for an update - and those are ordinary events during the slow,
/// disk-bound minutes a migration occupies. The journal is what turns "half-migrated data,
/// nobody knows" into "the next launch notices, restores the snapshot and tries again": it is
/// written before the first byte changes and deleted only once the run has committed, so its
/// mere presence at startup means the previous attempt did not finish.
/// </remarks>
public sealed class MigrationJournalEntry
{
    /// <summary>Creates an entry.</summary>
    public MigrationJournalEntry(
        string sessionId,
        DateTimeOffset startedUtc,
        InstallationModel model,
        MigrationDirection direction,
        Version fromVersion,
        Version toVersion,
        string originalDirectory,
        string workingDirectory,
        string? backupDirectory,
        MigrationPhase phase,
        string? lastCompletedStepId)
    {
        SessionId = sessionId;
        StartedUtc = startedUtc;
        Model = model;
        Direction = direction;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        OriginalDirectory = originalDirectory;
        WorkingDirectory = workingDirectory;
        BackupDirectory = backupDirectory;
        Phase = phase;
        LastCompletedStepId = lastCompletedStepId;
    }

    /// <summary>Identifies this run; also names the run's temporary directories.</summary>
    public string SessionId { get; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>The installation model the run used.</summary>
    public InstallationModel Model { get; }

    /// <summary>The direction the run was going in.</summary>
    public MigrationDirection Direction { get; }

    /// <summary>The version the run started from - the version to recover back to.</summary>
    public Version FromVersion { get; }

    /// <summary>The version the run was heading for.</summary>
    public Version ToVersion { get; }

    /// <summary>The original data directory.</summary>
    public string OriginalDirectory { get; }

    /// <summary>The directory the run was writing to.</summary>
    public string WorkingDirectory { get; }

    /// <summary>The snapshot to restore from, when the strategy took one.</summary>
    public string? BackupDirectory { get; }

    /// <summary>How far the run had got.</summary>
    public MigrationPhase Phase { get; set; }

    /// <summary>The last step that finished, for diagnostics.</summary>
    public string? LastCompletedStepId { get; set; }
}

/// <summary>
/// Reads and writes the <see cref="MigrationJournalEntry"/> for the in-flight run.
/// </summary>
public interface IMigrationJournal
{
    /// <summary>
    /// Returns the entry left behind by a run that never finished, or <see langword="null"/>
    /// when the last run completed cleanly.
    /// </summary>
    MigrationJournalEntry? Read();

    /// <summary>Persists <paramref name="entry"/> durably before the engine moves on.</summary>
    void Write(MigrationJournalEntry entry);

    /// <summary>Removes the journal, marking the run as finished.</summary>
    void Clear();
}

/// <summary>
/// The default <see cref="IMigrationJournal"/>: one file in the backup root.
/// </summary>
/// <remarks>
/// It deliberately does not live in the data directory. The whole point of a journal is to
/// survive the operations that replace that directory wholesale.
/// </remarks>
public sealed class FileMigrationJournal : IMigrationJournal
{
    /// <summary>The file name used inside the backup root.</summary>
    public const string DefaultFileName = "migration.journal";

    private readonly string _path;

    /// <summary>Creates a journal in <paramref name="backupRootDirectory"/>.</summary>
    public FileMigrationJournal(string backupRootDirectory, string fileName = DefaultFileName)
    {
        if (string.IsNullOrWhiteSpace(backupRootDirectory))
            throw new ArgumentException("A backup root directory is required.", nameof(backupRootDirectory));

        _path = Path.Combine(backupRootDirectory, fileName);
    }

    /// <summary>The full path of the journal file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public MigrationJournalEntry? Read()
    {
        Dictionary<string, string>? values = KeyValueFile.Read(_path);
        if (values == null)
            return null;

        try
        {
            return new MigrationJournalEntry(
                Get(values, "sessionId"),
                DateTimeOffset.Parse(Get(values, "startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (InstallationModel)Enum.Parse(typeof(InstallationModel), Get(values, "model")),
                (MigrationDirection)Enum.Parse(typeof(MigrationDirection), Get(values, "direction")),
                Version.Parse(Get(values, "fromVersion")),
                Version.Parse(Get(values, "toVersion")),
                Get(values, "originalDirectory"),
                Get(values, "workingDirectory"),
                GetOrNull(values, "backupDirectory"),
                (MigrationPhase)Enum.Parse(typeof(MigrationPhase), Get(values, "phase")),
                GetOrNull(values, "lastCompletedStepId"));
        }
        catch (Exception)
        {
            // A journal we cannot parse is a journal we cannot act on. Treating it as absent
            // and letting the normal version comparison decide is far safer than guessing at a
            // recovery from fields we do not trust.
            return null;
        }
    }

    /// <inheritdoc />
    public void Write(MigrationJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        KeyValueFile.Write(
            _path,
            [
                new KeyValuePair<string, string>("sessionId", entry.SessionId),
                new KeyValuePair<string, string>("startedUtc", entry.StartedUtc.ToString("O", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("model", entry.Model.ToString()),
                new KeyValuePair<string, string>("direction", entry.Direction.ToString()),
                new KeyValuePair<string, string>("fromVersion", entry.FromVersion.ToString()),
                new KeyValuePair<string, string>("toVersion", entry.ToVersion.ToString()),
                new KeyValuePair<string, string>("originalDirectory", entry.OriginalDirectory),
                new KeyValuePair<string, string>("workingDirectory", entry.WorkingDirectory),
                new KeyValuePair<string, string>("backupDirectory", entry.BackupDirectory ?? string.Empty),
                new KeyValuePair<string, string>("phase", entry.Phase.ToString()),
                new KeyValuePair<string, string>("lastCompletedStepId", entry.LastCompletedStepId ?? string.Empty),
            ],
            header: "Barbatos.Migration in-flight run. If this file exists at startup, the previous migration did not finish.");
    }

    /// <inheritdoc />
    public void Clear() => KeyValueFile.Delete(_path);

    private static string Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : throw new FormatException($"Journal is missing '{key}'.");

    private static string? GetOrNull(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;
}
