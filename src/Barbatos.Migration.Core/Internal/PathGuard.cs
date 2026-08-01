// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.


namespace Barbatos.Migration.Internal;

/// <summary>
/// Path checks the engine runs before it deletes or copies anything.
/// </summary>
internal static class PathGuard
{
    private static readonly StringComparison PathComparison =
        Environment.OSVersion.Platform == PlatformID.Unix
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Normalises to a full path without a trailing separator, so two spellings of the same
    /// directory compare equal.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        string full = Path.GetFullPath(path);
        if (full.Length > 3 && (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
            full = full[..^1];

        return full;
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same directory.</summary>
    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>
    /// Whether <paramref name="candidate"/> sits inside <paramref name="ancestor"/> (or is it).
    /// </summary>
    public static bool IsInside(string candidate, string ancestor)
    {
        string normalizedCandidate = Normalize(candidate);
        string normalizedAncestor = Normalize(ancestor);

        if (string.Equals(normalizedCandidate, normalizedAncestor, PathComparison))
            return true;

        return normalizedCandidate.Length > normalizedAncestor.Length
            && normalizedCandidate.StartsWith(normalizedAncestor, PathComparison)
            && (normalizedCandidate[normalizedAncestor.Length] == Path.DirectorySeparatorChar
                || normalizedCandidate[normalizedAncestor.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Rejects a backup location that overlaps the data it is meant to protect. Nesting the
    /// backup inside the data directory makes the snapshot copy itself, and makes restoring
    /// the snapshot delete the snapshot.
    /// </summary>
    public static void EnsureDisjoint(string dataDirectory, string backupDirectory)
    {
        if (IsInside(backupDirectory, dataDirectory))
        {
            throw new MigrationException(
                $"The backup directory '{backupDirectory}' is inside the data directory '{dataDirectory}'. " +
                "The snapshot would then contain itself, and restoring it would delete it. " +
                "Point MigrationOptions.BackupRootDirectory somewhere outside the data directory " +
                "(the default sits beside it, not inside it).");
        }

        if (IsInside(dataDirectory, backupDirectory))
        {
            throw new MigrationException(
                $"The data directory '{dataDirectory}' is inside the backup directory '{backupDirectory}'. " +
                "Clearing old backups would delete the live data.");
        }
    }

    /// <summary>
    /// Refuses to let the engine take a whole drive, a user profile root or a very short path
    /// as its data or backup directory - a mistyped configuration value must not turn into a
    /// recursive delete of someone's home folder.
    /// </summary>
    public static void EnsureSafeToDelete(string path, string parameterName)
    {
        string full = Normalize(path);

        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && string.Equals(full, Normalize(root!), PathComparison))
            throw new MigrationException($"'{full}' is a drive root and cannot be used as {parameterName}.");

        int separators = 0;
        for (int i = 0; i < full.Length; i++)
        {
            if (full[i] == Path.DirectorySeparatorChar || full[i] == Path.AltDirectorySeparatorChar)
                separators++;
        }

        if (separators < 2)
        {
            throw new MigrationException(
                $"'{full}' is too close to the drive root to be used as {parameterName}. " +
                "Use a dedicated per-application directory, e.g. the one from IFileSystem.AppDataDirectory.");
        }

        foreach (Environment.SpecialFolder folder in ProtectedFolders)
        {
            string special = Environment.GetFolderPath(folder);
            if (special.Length > 0 && AreSame(special, full))
                throw new MigrationException($"'{full}' is a well-known system folder and cannot be used as {parameterName}.");
        }
    }

    private static readonly Environment.SpecialFolder[] ProtectedFolders =
    [
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.Desktop,
    ];
}
