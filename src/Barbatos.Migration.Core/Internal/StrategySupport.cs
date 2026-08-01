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
            progress == null ? null : new PhaseProgress(progress, phase, message),
            cancellationToken);

        Report(progress, phase, 100, message);
    }

    /// <summary>
    /// Rescales the copy's 0-100 into a phase report, synchronously.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="Progress{T}"/>. That class posts through the captured
    /// <see cref="SynchronizationContext"/>, so its callbacks arrive on another thread after an
    /// unbounded delay - which here means after <see cref="ProgressRelay.Offset"/> and
    /// <see cref="ProgressRelay.Span"/> have already been moved on to a later phase, and a
    /// report from the snapshot gets rescaled into the migration's slice of the bar. On a caller
    /// that runs the engine on its UI thread it is worse still: the whole copy blocks that
    /// thread, so not one queued callback runs until the copy has finished.
    /// </remarks>
    private sealed class PhaseProgress : IProgress<double>
    {
        private readonly IProgress<MigrationProgress> _inner;
        private readonly MigrationPhase _phase;
        private readonly string _message;

        public PhaseProgress(IProgress<MigrationProgress> inner, MigrationPhase phase, string message)
        {
            _inner = inner;
            _phase = phase;
            _message = message;
        }

        public void Report(double percentage) =>
            StrategySupport.Report(
                _inner,
                _phase,
                percentage,
                string.Format(CultureInfo.CurrentCulture, "{0} {1:F0}%", _message, percentage));
    }
}
