// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.


namespace Barbatos.Migration.Internal;

/// <summary>
/// The directory primitives the installation strategies are built on. Everything here is
/// written for the awkward realities of a real user's machine: files held open by an
/// anti-virus scanner, read-only attributes, directories on different volumes, and a process
/// that can be killed at any instruction boundary.
/// </summary>
internal static class DirectoryOperations
{
    private const int RetryCount = 5;
    private const int RetryDelayMilliseconds = 120;

    /// <summary>Total size of a directory tree, in bytes. Returns 0 when it does not exist.</summary>
    public static long GetSize(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // Raced with a delete; the size is an estimate for progress and free-space
                // checks, so a vanished file simply does not count.
            }
            catch (IOException)
            {
            }
        }

        return total;
    }

    /// <summary>
    /// Free space on the volume holding <paramref name="path"/>, or <see langword="null"/> when
    /// it cannot be determined (network shares, some Unix mounts).
    /// </summary>
    public static long? GetAvailableFreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;

            return new DriveInfo(root!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a directory tree, reporting byte-level progress. <paramref name="totalBytes"/>
    /// comes from <see cref="GetSize"/>; pass 0 to report no progress.
    /// </summary>
    public static void Copy(
        string source,
        string target,
        long totalBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long copied = 0;
        CopyCore(source, target, totalBytes, ref copied, progress, cancellationToken);
    }

    private static void CopyCore(
        string source,
        string target,
        long totalBytes,
        ref long copiedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destination = Path.Combine(target, Path.GetFileName(file));
            long length = 0;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }

            File.Copy(file, destination, overwrite: true);

            // A copied read-only file would block the restore that overwrites it later.
            ClearReadOnly(destination);

            copiedBytes += length;
            if (progress != null && totalBytes > 0)
                progress.Report(Math.Min(100.0, copiedBytes * 100.0 / totalBytes));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyCore(directory, Path.Combine(target, Path.GetFileName(directory)), totalBytes, ref copiedBytes, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Moves a directory, falling back to copy-then-delete when the two ends are on different
    /// volumes. On the same volume this is a rename: atomic, instant, and the reason the engine
    /// can swap directories without a window where neither copy is complete.
    /// </summary>
    public static void Move(string source, string target, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(target))
            throw new IOException($"Cannot move '{source}' onto '{target}': the target already exists.");

        Directory.CreateDirectory(Path.GetDirectoryName(PathGuard.Normalize(target))!);

        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException)
        {
            // Cross-volume, or a transient lock. Fall through to copy + delete.
        }
        catch (UnauthorizedAccessException)
        {
        }

        long size = GetSize(source, cancellationToken);
        Copy(source, target, size, progress: null, cancellationToken);
        Delete(source);
    }

    /// <summary>
    /// Deletes a directory tree, clearing read-only attributes and retrying briefly - on
    /// Windows an indexer or an anti-virus scanner routinely holds a handle for a moment after
    /// the app has closed its own.
    /// </summary>
    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        Exception? last = null;
        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                ClearReadOnlyRecursive(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            Thread.Sleep(RetryDelayMilliseconds * (attempt + 1));
        }

        throw new IOException($"Could not delete '{directory}' after {RetryCount} attempts.", last);
    }

    /// <summary>
    /// Deletes without throwing. Used for best-effort cleanup, where failing to remove a
    /// temporary folder must not turn a successful migration into a failed one.
    /// </summary>
    public static bool TryDelete(string directory, IMigrationLogger? logger = null)
    {
        try
        {
            Delete(directory);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Log(MigrationLogLevel.Warning, $"Could not delete '{directory}'.", ex);
            return false;
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="replacement"/> as close to
    /// atomically as the file system allows: the existing directory is renamed aside first, the
    /// replacement is renamed into place, and only then is the old copy deleted.
    /// </summary>
    /// <remarks>
    /// The naive "delete the target, then copy the replacement in" costs the user everything if
    /// the process dies between the two - which is exactly when it matters. Here the only
    /// window is between two renames, and both orderings that a crash can leave behind are
    /// recoverable: either <paramref name="target"/> still exists (nothing happened) or
    /// <paramref name="discardDirectory"/> holds the previous contents and can be renamed back.
    /// </remarks>
    public static void Replace(string target, string replacement, string discardDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(replacement))
            throw new DirectoryNotFoundException($"The replacement directory '{replacement}' does not exist.");

        Delete(discardDirectory);

        bool targetExisted = Directory.Exists(target);
        if (targetExisted)
            Move(target, discardDirectory, cancellationToken);

        try
        {
            Move(replacement, target, cancellationToken);
        }
        catch
        {
            // Put the original back before rethrowing, so a failure here leaves the user
            // exactly where they started rather than with no data directory at all.
            if (targetExisted && !Directory.Exists(target))
                Move(discardDirectory, target, CancellationToken.None);

            throw;
        }

        TryDelete(discardDirectory);
    }

    /// <summary>Creates the directory and returns its normalised full path.</summary>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return PathGuard.Normalize(directory);
    }

    /// <summary>Whether the directory exists and holds at least one file or subdirectory.</summary>
    public static bool HasContent(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).GetEnumerator().MoveNext();

    private static void ClearReadOnly(string file)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
            // Best effort: if the attribute cannot be cleared the subsequent operation will
            // fail with a message that actually describes the problem.
        }
    }

    private static void ClearReadOnlyRecursive(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            ClearReadOnly(file);
    }
}
