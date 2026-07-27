// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.Migration.Internal;

/// <summary>
/// Bits both installation strategies need: progress reporting, human-readable sizes, and the
/// free-space pre-flight check.
/// </summary>
internal static class StrategySupport
{
    public static void Report(IProgress<MigrationProgress>? progress, MigrationPhase phase, double percentage, string detail) =>
        progress?.Report(new MigrationProgress(phase, percentage, detail, string.Empty, string.Empty, null, isIndeterminate: false));

    /// <summary>
    /// Refuses to start a copy that cannot finish. Running out of disk halfway through a
    /// snapshot leaves the engine trying to roll back on a full volume, which is the one
    /// situation where rollback itself is likely to fail - so this converts it into a clear
    /// message before anything has been written.
    /// </summary>
    public static void EnsureEnoughFreeSpace(MigrationOptions options, long dataSize, string targetRoot)
    {
        if (options.SkipFreeSpaceCheck || dataSize == 0)
            return;

        long? free = DirectoryOperations.GetAvailableFreeSpace(targetRoot);
        if (free == null)
            return;

        long required = (long)(dataSize * options.RequiredFreeSpaceFactor);
        if (free.Value >= required)
            return;

        throw new MigrationException(
            "Not enough free disk space to copy your data before updating. " +
            $"{FormatSize(required)} is needed on '{Path.GetPathRoot(Path.GetFullPath(targetRoot))}' " +
            $"but only {FormatSize(free.Value)} is available. Free some space and start the application again.");
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }

    /// <summary>
    /// Copies a tree with byte-accurate progress reported into the given phase.
    /// </summary>
    public static void CopyWithProgress(
        string source,
        string target,
        long size,
        MigrationPhase phase,
        string message,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, phase, 0, message);

        DirectoryOperations.Copy(
            source,
            target,
            size,
            new Progress<double>(percent => Report(progress, phase, percent, $"{message} {percent:F0}%")),
            cancellationToken);

        Report(progress, phase, 100, message);
    }
}
