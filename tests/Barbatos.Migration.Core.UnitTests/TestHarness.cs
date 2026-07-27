// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// A throwaway directory tree plus the helpers every test needs: seed a file, assert on a
/// file, build an engine over it.
/// </summary>
public sealed class TestHarness : IDisposable
{
    public TestHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "barbatos-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        DataDirectory = Path.Combine(Root, "Data");
        Directory.CreateDirectory(DataDirectory);
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string BackupRoot => Path.Combine(Root, ".migration");

    public List<string> LogMessages { get; } = [];

    public IMigrationLogger Logger => new DelegateMigrationLogger((level, message, _) => LogMessages.Add($"{level}: {message}"));

    public MigrationOptions CreateOptions(Action<MigrationOptions>? configure = null)
    {
        MigrationOptions options = new()
        {
            DataDirectory = DataDirectory,
            BackupRootDirectory = BackupRoot,
            TargetDataVersion = new Version(2, 0, 0),
            InitialDataVersion = new Version(1, 0, 0),
            Logger = Logger,
            SkipFreeSpaceCheck = true,
        };

        configure?.Invoke(options);
        return options;
    }

    public void WriteFile(string relativePath, string content, string? baseDirectory = null)
    {
        string path = Path.Combine(baseDirectory ?? DataDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string ReadFile(string relativePath, string? baseDirectory = null) =>
        File.ReadAllText(Path.Combine(baseDirectory ?? DataDirectory, relativePath));

    public bool FileExists(string relativePath, string? baseDirectory = null) =>
        File.Exists(Path.Combine(baseDirectory ?? DataDirectory, relativePath));

    public void StampVersion(Version version, string? directory = null) =>
        new FileDataVersionStore(directory ?? DataDirectory).Write(version, []);

    public Version? ReadStampedVersion(string? directory = null) =>
        new FileDataVersionStore(directory ?? DataDirectory).Read();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A provider that runs a delegate and records that it ran.</summary>
public sealed class RecordingProvider : IMigrationProvider
{
    private readonly Func<IMigrationContext, CancellationToken, Task> _work;

    public RecordingProvider(string name, Func<IMigrationContext, CancellationToken, Task>? work = null, bool canDown = false, double weight = 1.0)
    {
        Name = name;
        _work = work ?? ((_, _) => Task.CompletedTask);
        CanDown = canDown;
        Weight = weight;
    }

    public string Name { get; }

    public double Weight { get; }

    public bool CanDown { get; }

    public int UpCallCount { get; private set; }

    public int DownCallCount { get; private set; }

    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        UpCallCount++;
        progress?.Report(new MigrationProgress(50, $"{Name} halfway"));
        return _work(context, cancellationToken);
    }

    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!CanDown)
            throw new NotSupportedException(Name);

        DownCallCount++;
        return _work(context, cancellationToken);
    }
}
