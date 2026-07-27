// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Globalization;
using System.Text;

namespace Barbatos.Migration;

/// <summary>
/// Guarantees that only one process migrates a given data directory at a time.
/// </summary>
/// <remarks>
/// Two copies of the app started from a double-clicked shortcut, or an app racing its own
/// updater, will both find the same out-of-date data and both start migrating it. Single-
/// instance enforcement at the application level is not enough: an installer or a repair tool
/// is a different executable entirely. The lock lives beside the data it protects, so it covers
/// every process that can reach that folder.
/// </remarks>
public interface IMigrationLock
{
    /// <summary>
    /// Takes the lock, or returns <see langword="null"/> when another process holds it.
    /// Disposing the returned handle releases it.
    /// </summary>
    IDisposable? TryAcquire();
}

/// <summary>
/// The default <see cref="IMigrationLock"/>: an exclusively opened lock file in the backup
/// root, which the operating system releases even if the process is killed.
/// </summary>
public sealed class FileMigrationLock : IMigrationLock
{
    /// <summary>The file name used inside the backup root.</summary>
    public const string DefaultFileName = "migration.lock";

    private readonly string _path;

    /// <summary>Creates a lock in <paramref name="backupRootDirectory"/>.</summary>
    public FileMigrationLock(string backupRootDirectory, string fileName = DefaultFileName)
    {
        if (string.IsNullOrWhiteSpace(backupRootDirectory))
            throw new ArgumentException("A backup root directory is required.", nameof(backupRootDirectory));

        _path = Path.Combine(backupRootDirectory, fileName);
    }

    /// <summary>The full path of the lock file.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public IDisposable? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            // FileShare.None is the lock. DeleteOnClose keeps the directory tidy, and because
            // the handle is owned by the OS rather than by our code, a process killed mid-run
            // releases it immediately - no stale-lock timeout to guess at.
            FileStream stream = new(
                _path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128,
                FileOptions.DeleteOnClose);

            byte[] owner = Encoding.UTF8.GetBytes(string.Format(
                CultureInfo.InvariantCulture,
                "pid={0} started={1:O}",
                GetCurrentProcessId(),
                DateTimeOffset.UtcNow));

            stream.Write(owner, 0, owner.Length);
            stream.Flush();

            return stream;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int GetCurrentProcessId()
    {
#if NET8_0_OR_GREATER
        return Environment.ProcessId;
#else
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        return process.Id;
#endif
    }
}
