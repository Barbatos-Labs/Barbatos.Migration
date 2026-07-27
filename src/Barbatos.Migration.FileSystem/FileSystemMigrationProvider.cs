// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.


namespace Barbatos.Migration.FileSystem;

/// <summary>
/// Restructures the data directory: moving files and folders, renaming them, deleting what a
/// new version no longer reads, and creating what it now expects.
/// </summary>
/// <remarks>
/// Operations are declared up front rather than written as code so that each one can state its
/// own inverse. That is what lets a whole reorganisation be undone by
/// <see cref="DownAsync"/> without anybody writing the reverse of it by hand - and getting the
/// order wrong.
/// </remarks>
/// <example>
/// <code>
/// new FileSystemMigrationProvider("Reorganise the data folder", operations => operations
///     .EnsureDirectory("assets")
///     .MoveDirectory("images", "assets/images")
///     .RenameFile("data.sqlite", "app.db")
///     .DeleteFile("thumbnail.cache"));
/// </code>
/// </example>
public class FileSystemMigrationProvider : IMigrationProvider
{
    private readonly List<FileSystemOperation> _operations;

    /// <summary>Creates the provider.</summary>
    /// <param name="name">The name shown in logs and progress UI.</param>
    /// <param name="configure">Declares the operations, in the order they should run.</param>
    public FileSystemMigrationProvider(string name, Action<FileSystemOperationBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A provider name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(configure);

        FileSystemOperationBuilder builder = new();
        configure(builder);

        _operations = builder.Build();
        if (_operations.Count == 0)
            throw new ArgumentException($"'{name}' declares no file system operations.", nameof(configure));

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public virtual double Weight => 1.0;

    /// <inheritdoc />
    public bool CanDown
    {
        get
        {
            foreach (FileSystemOperation operation in _operations)
            {
                if (!operation.IsReversible)
                    return false;
            }

            return true;
        }
    }

    /// <inheritdoc />
    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        for (int i = 0; i < _operations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileSystemOperation operation = _operations[i];
            Report(progress, i, _operations.Count, operation.Describe(forward: true));
            operation.Apply(context.WorkingDirectory, forward: true);
        }

        progress?.Report(new MigrationProgress(100, Name));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!CanDown)
            throw new NotSupportedException($"'{Name}' contains an irreversible operation and cannot be undone.");

        // Reverse order: undoing "create the folder, then move a file into it" has to move the
        // file back out before the folder can go.
        for (int i = _operations.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileSystemOperation operation = _operations[i];
            Report(progress, _operations.Count - 1 - i, _operations.Count, operation.Describe(forward: false));
            operation.Apply(context.WorkingDirectory, forward: false);
        }

        progress?.Report(new MigrationProgress(100, Name));
        return Task.CompletedTask;
    }

    private static void Report(IProgress<MigrationProgress>? progress, int index, int total, string detail) =>
        progress?.Report(new MigrationProgress(index * 100.0 / total, detail));
}

/// <summary>Declares the operations a <see cref="FileSystemMigrationProvider"/> performs.</summary>
public sealed class FileSystemOperationBuilder
{
    private readonly List<FileSystemOperation> _operations = [];

    /// <summary>Moves or renames a file. Reversible.</summary>
    public FileSystemOperationBuilder MoveFile(string from, string to) =>
        Add(new FileSystemOperation(FileSystemOperationKind.MoveFile, from, to));

    /// <inheritdoc cref="MoveFile"/>
    public FileSystemOperationBuilder RenameFile(string from, string to) => MoveFile(from, to);

    /// <summary>Moves or renames a directory and everything in it. Reversible.</summary>
    public FileSystemOperationBuilder MoveDirectory(string from, string to) =>
        Add(new FileSystemOperation(FileSystemOperationKind.MoveDirectory, from, to));

    /// <inheritdoc cref="MoveDirectory"/>
    public FileSystemOperationBuilder RenameDirectory(string from, string to) => MoveDirectory(from, to);

    /// <summary>Copies a file, leaving the original in place. Reversed by deleting the copy.</summary>
    public FileSystemOperationBuilder CopyFile(string from, string to) =>
        Add(new FileSystemOperation(FileSystemOperationKind.CopyFile, from, to));

    /// <summary>
    /// Deletes a file. Irreversible, which makes the whole provider forward-only - the
    /// engine's snapshot is what protects the data, not the operation.
    /// </summary>
    public FileSystemOperationBuilder DeleteFile(string path) =>
        Add(new FileSystemOperation(FileSystemOperationKind.DeleteFile, path, null));

    /// <summary>Deletes a directory and its contents. Irreversible.</summary>
    public FileSystemOperationBuilder DeleteDirectory(string path) =>
        Add(new FileSystemOperation(FileSystemOperationKind.DeleteDirectory, path, null));

    /// <summary>Creates a directory if it does not exist. Reversed by deleting it when empty.</summary>
    public FileSystemOperationBuilder EnsureDirectory(string path) =>
        Add(new FileSystemOperation(FileSystemOperationKind.EnsureDirectory, path, null));

    /// <summary>Writes a text file, replacing any existing content. Reversed by deleting it.</summary>
    public FileSystemOperationBuilder WriteText(string path, string content) =>
        Add(new FileSystemOperation(FileSystemOperationKind.WriteText, path, null, content));

    private FileSystemOperationBuilder Add(FileSystemOperation operation)
    {
        _operations.Add(operation);
        return this;
    }

    internal List<FileSystemOperation> Build() => _operations;
}

internal enum FileSystemOperationKind
{
    MoveFile,
    MoveDirectory,
    CopyFile,
    DeleteFile,
    DeleteDirectory,
    EnsureDirectory,
    WriteText,
}

internal sealed class FileSystemOperation
{
    private readonly FileSystemOperationKind _kind;
    private readonly string _source;
    private readonly string? _target;
    private readonly string? _content;

    public FileSystemOperation(FileSystemOperationKind kind, string source, string? target, string? content = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A source path is required.", nameof(source));
        if (target != null && string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("A target path cannot be blank.", nameof(target));

        _kind = kind;
        _source = source;
        _target = target;
        _content = content;
    }

    public bool IsReversible =>
        _kind is not (FileSystemOperationKind.DeleteFile or FileSystemOperationKind.DeleteDirectory);

    public string Describe(bool forward) => _kind switch
    {
        FileSystemOperationKind.MoveFile or FileSystemOperationKind.MoveDirectory =>
            forward ? $"Moving {_source} to {_target}" : $"Moving {_target} back to {_source}",
        FileSystemOperationKind.CopyFile => forward ? $"Copying {_source} to {_target}" : $"Removing {_target}",
        FileSystemOperationKind.DeleteFile or FileSystemOperationKind.DeleteDirectory => $"Deleting {_source}",
        FileSystemOperationKind.EnsureDirectory => forward ? $"Creating {_source}" : $"Removing {_source}",
        FileSystemOperationKind.WriteText => forward ? $"Writing {_source}" : $"Removing {_source}",
        _ => _source,
    };

    public void Apply(string root, bool forward)
    {
        string source = Resolve(root, _source);
        string? target = _target == null ? null : Resolve(root, _target);

        switch (_kind)
        {
            case FileSystemOperationKind.MoveFile:
                MoveFile(forward ? source : target!, forward ? target! : source);
                break;

            case FileSystemOperationKind.MoveDirectory:
                MoveDirectory(forward ? source : target!, forward ? target! : source);
                break;

            case FileSystemOperationKind.CopyFile:
                if (forward)
                {
                    if (File.Exists(source))
                    {
                        EnsureParent(target!);
                        File.Copy(source, target!, overwrite: true);
                    }
                }
                else
                {
                    DeleteFile(target!);
                }

                break;

            case FileSystemOperationKind.DeleteFile:
                if (forward)
                    DeleteFile(source);
                break;

            case FileSystemOperationKind.DeleteDirectory:
                if (forward && Directory.Exists(source))
                    Directory.Delete(source, recursive: true);
                break;

            case FileSystemOperationKind.EnsureDirectory:
                if (forward)
                {
                    Directory.CreateDirectory(source);
                }
                else if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).GetEnumerator().MoveNext())
                {
                    // Only remove it if the undo left it empty; a directory the user has since
                    // filled is not ours to delete.
                    Directory.Delete(source);
                }

                break;

            case FileSystemOperationKind.WriteText:
                if (forward)
                {
                    EnsureParent(source);
                    File.WriteAllText(source, _content ?? string.Empty);
                }
                else
                {
                    DeleteFile(source);
                }

                break;
        }
    }

    private static void MoveFile(string from, string to)
    {
        // Absent source, present target: already done. Re-running a half-applied step must be
        // a no-op rather than an error.
        if (!File.Exists(from))
            return;

        EnsureParent(to);
        if (File.Exists(to))
            File.Delete(to);

        File.Move(from, to);
    }

    private static void MoveDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
            return;

        EnsureParent(to);
        if (Directory.Exists(to))
            Directory.Delete(to, recursive: true);

        Directory.Move(from, to);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void EnsureParent(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent!);
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new MigrationException(
                $"'{relative}' is an absolute path. File system migration operations are always relative to the " +
                "working directory, so that the same step works for both installation models.");
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // "../.." in a migration script would reach outside the data directory, which is the
        // one place a migration is allowed to touch.
        if (!full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationException($"'{relative}' resolves outside the working directory ('{full}').");
        }

        return full;
    }
}
