// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.Migration.Internal;

namespace Barbatos.Migration;

/// <summary>
/// Remembers what version the data on disk is at, and which steps have been applied to it.
/// </summary>
/// <remarks>
/// This is the piece that makes the whole framework work across launches. Without a persisted
/// data version there is nothing to compare the application version against, so every launch
/// would either re-run every migration or run none of them. Note that the data version is
/// <em>not</em> the application version: it only moves when a migration step moves it, so an
/// app can ship 1.4.1, 1.4.2 and 1.4.3 without any data change at all.
/// </remarks>
public interface IDataVersionStore
{
    /// <summary>
    /// Reads the current data version, or <see langword="null"/> when the data has never been
    /// stamped - a fresh install, or an install that predates this framework.
    /// </summary>
    Version? Read();

    /// <summary>Reads the ids of the steps applied so far, oldest first.</summary>
    IReadOnlyList<string> ReadAppliedStepIds();

    /// <summary>
    /// Stamps the data with <paramref name="version"/> and records the applied step ids. Must
    /// be durable by the time it returns: the engine calls it as the very last act of a
    /// successful run, and a crash immediately afterwards must not re-run the migration.
    /// </summary>
    void Write(Version version, IReadOnlyList<string> appliedStepIds);
}

/// <summary>
/// The default <see cref="IDataVersionStore"/>: a single <c>.migration-version</c> file inside
/// the data directory it describes.
/// </summary>
/// <remarks>
/// Keeping the stamp inside the data directory rather than next to it is deliberate - it means
/// a directory that gets copied, cloned or restored carries its version with it. That is what
/// lets the side-by-side strategy clone a folder and have the clone report the right starting
/// version, and it is what makes a snapshot restore put the version back too.
/// </remarks>
public sealed class FileDataVersionStore : IDataVersionStore
{
    /// <summary>The file name used inside the data directory.</summary>
    public const string DefaultFileName = ".migration-version";

    private const string VersionKey = "version";
    private const string StepsKey = "steps";
    private const string UpdatedKey = "updatedUtc";

    private readonly string _path;

    /// <summary>Creates a store over <paramref name="dataDirectory"/>.</summary>
    /// <param name="dataDirectory">The directory whose version is being tracked.</param>
    /// <param name="fileName">The file name to use; defaults to <see cref="DefaultFileName"/>.</param>
    public FileDataVersionStore(string dataDirectory, string fileName = DefaultFileName)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));

        _path = Path.Combine(dataDirectory, fileName);
    }

    /// <summary>The full path of the version file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public Version? Read()
    {
        Dictionary<string, string>? values = KeyValueFile.Read(_path);
        if (values == null || !values.TryGetValue(VersionKey, out string? raw))
            return null;

        return Version.TryParse(raw, out Version? version) ? version : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadAppliedStepIds()
    {
        Dictionary<string, string>? values = KeyValueFile.Read(_path);
        if (values == null || !values.TryGetValue(StepsKey, out string? raw) || raw.Length == 0)
            return [];

        return raw.Split(['|'], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <inheritdoc />
    public void Write(Version version, IReadOnlyList<string> appliedStepIds)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Steps accumulate across runs: the ledger is the full history of what this copy of the
        // data has been through, not just the last run.
        List<string> steps = [.. ReadAppliedStepIds()];
        foreach (string id in appliedStepIds ?? (IReadOnlyList<string>)[])
        {
            if (!steps.Contains(id, StringComparer.Ordinal))
                steps.Add(id);
        }

        KeyValueFile.Write(
            _path,
            [
                new KeyValuePair<string, string>(VersionKey, version.ToString()),
                new KeyValuePair<string, string>(StepsKey, string.Join("|", steps)),
                new KeyValuePair<string, string>(UpdatedKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ],
            header: "Barbatos.Migration data version stamp - do not edit by hand.");
    }
}
