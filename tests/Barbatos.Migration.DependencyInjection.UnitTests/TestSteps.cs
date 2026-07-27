// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration.DependencyInjection.UnitTests;

/// <summary>A throwaway data directory.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "barbatos-migration-di-tests", Guid.NewGuid().ToString("N"));
        Data = Path.Combine(Root, "Data");
        Directory.CreateDirectory(Data);
    }

    public string Root { get; }

    public string Data { get; }

    public string BackupRoot => Path.Combine(Root, ".migration");

    public void Stamp(Version version) => new FileDataVersionStore(Data).Write(version, []);

    public Version? ReadVersion() => new FileDataVersionStore(Data).Read();

    public bool FileExists(string relativePath) => File.Exists(Path.Combine(Data, relativePath));

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

/// <summary>Records that it ran, so a test can assert the container actually wired it up.</summary>
public sealed class MarkerProvider(string name, string fileName) : IMigrationProvider
{
    public string Name { get; } = name;

    public double Weight => 1.0;

    public bool CanDown => true;

    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        File.WriteAllText(context.GetWorkingPath(fileName), Name);
        return Task.CompletedTask;
    }

    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        File.Delete(context.GetWorkingPath(fileName));
        return Task.CompletedTask;
    }
}

/// <summary>Settings the container injects into a step, to prove constructor injection works.</summary>
public sealed class StorageSettings
{
    public string IndexFileName { get; set; } = "index.dat";
}

/// <summary>A step that takes a dependency - the reason to register steps rather than construct them.</summary>
[MigrationStep("1.5.0", "Builds the index from injected settings")]
public sealed class IndexStep(StorageSettings settings) : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        File.WriteAllText(context.GetWorkingPath(settings.IndexFileName), "built");
        return Task.CompletedTask;
    }
}

/// <summary>Discovered by scanning, and constructible without arguments.</summary>
[MigrationStep("1.8.0", "Discovered by scanning")]
public sealed class ScannedStep : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        File.WriteAllText(context.GetWorkingPath("scanned.txt"), "found");
        return Task.CompletedTask;
    }
}

/// <summary>Registered by type through <c>AddStep&lt;TStep&gt;()</c>.</summary>
[MigrationStep("1.9.0", "Registered by type")]
public sealed class TypeRegisteredStep : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        File.WriteAllText(context.GetWorkingPath("by-type.txt"), "registered");
        return Task.CompletedTask;
    }
}
