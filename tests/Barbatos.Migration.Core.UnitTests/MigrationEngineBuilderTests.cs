// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// Every method on <see cref="MigrationEngineBuilder"/> - the no-container API, and the one a
/// console tool or a game reaches for.
/// </summary>
public class MigrationEngineBuilderTests
{
    [Fact]
    public async Task The_fluent_surface_configures_an_engine_end_to_end()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        List<string> log = [];

        MigrationEngine engine = new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .StartingFromVersion(new Version(1, 0, 0))
            .LogTo((level, message, _) => log.Add($"{level}: {message}"))
            .Configure(options =>
            {
                options.SkipFreeSpaceCheck = true;
                options.BackupRetentionCount = 0;
            })
            .AddStep("2.0.0", "Writes a marker", (context, progress, _) =>
            {
                progress?.Report(new MigrationProgress(50, "halfway"));
                File.WriteAllText(context.GetWorkingPath("marker.txt"), "written by a delegate step");
                return Task.CompletedTask;
            })
            .Build();

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("marker.txt").Should().Be("written by a delegate step");
        log.Should().Contain(entry => entry.Contains("Migration complete"));
        result.BackupDirectory.Should().BeNull("BackupRetentionCount was 0");
    }

    [Fact]
    public async Task A_delegate_step_can_be_reversible()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngineBuilder Build(string target) => new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion(target)
            .StartingFromVersion(new Version(1, 0, 0))
            .Configure(options =>
            {
                options.SkipFreeSpaceCheck = true;
                options.AllowDowngrade = true;
            })
            .AddStep(
                "2.0.0",
                "Adds a file",
                up: (context, _, _) =>
                {
                    File.WriteAllText(context.GetWorkingPath("added.txt"), "hello");
                    return Task.CompletedTask;
                },
                down: (context, _, _) =>
                {
                    File.Delete(context.GetWorkingPath("added.txt"));
                    return Task.CompletedTask;
                });

        await Build("2.0.0").Build().RunAsync();
        harness.FileExists("added.txt").Should().BeTrue();

        MigrationResult down = await Build("1.0.0").Build().RunAsync();

        down.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.FileExists("added.txt").Should().BeFalse();
    }

    [Fact]
    public async Task UseSideBySideModel_points_the_engine_at_the_version_root()
    {
        using TestHarness harness = new();
        string root = Path.Combine(harness.Root, "Versions");
        Directory.CreateDirectory(Path.Combine(root, "1.0.0"));
        File.WriteAllText(Path.Combine(root, "1.0.0", "data.txt"), "v1");
        new FileDataVersionStore(Path.Combine(root, "1.0.0")).Write(new Version(1, 0, 0), []);

        MigrationResult result = await new MigrationEngineBuilder()
            .UseSideBySideModel(root)
            .TargetVersion("2.0.0")
            .Configure(options => options.SkipFreeSpaceCheck = true)
            .AddStep("2.0.0", "Upgrade", (context, _, _) =>
            {
                File.WriteAllText(context.GetWorkingPath("data.txt"), "v2");
                return Task.CompletedTask;
            })
            .Build()
            .RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        File.ReadAllText(Path.Combine(root, "2.0.0", "data.txt")).Should().Be("v2");
        File.ReadAllText(Path.Combine(root, "1.0.0", "data.txt")).Should().Be("v1");
    }

    [Fact]
    public async Task A_custom_journal_and_lock_replace_the_file_based_defaults()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        RecordingJournal journal = new();
        RecordingLock migrationLock = new(available: true);

        MigrationResult result = await new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .StartingFromVersion(new Version(1, 0, 0))
            .Configure(options => options.SkipFreeSpaceCheck = true)
            .UseJournal(journal)
            .UseLock(migrationLock)
            .AddStep("2.0.0", "Change", new RecordingProvider("p"))
            .Build()
            .RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        migrationLock.AcquireCount.Should().Be(1);
        migrationLock.Released.Should().BeTrue("the lock must not outlive the run");

        journal.Phases.Should().StartWith([MigrationPhase.Preparing, MigrationPhase.Migrating]);
        journal.Phases.Should().Contain(MigrationPhase.Committing);
        journal.Cleared.Should().BeTrue("a completed run leaves no journal behind");
    }

    [Fact]
    public async Task A_lock_held_elsewhere_blocks_the_run()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        RecordingProvider provider = new("never");

        MigrationResult result = await new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .UseLock(new RecordingLock(available: false))
            .AddStep("2.0.0", "Change", provider)
            .Build()
            .RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Blocked);
        provider.UpCallCount.Should().Be(0);
    }

    [Fact]
    public async Task AskBeforeMigrating_switches_to_manual_mode_and_registers_the_prompt()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        DecliningPrompt prompt = new();
        RecordingProvider provider = new("never");

        MigrationEngineBuilder builder = new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .StartingFromVersion(new Version(1, 0, 0))
            .Configure(options => options.SkipFreeSpaceCheck = true)
            .AskBeforeMigrating(prompt)
            .AddStep("2.0.0", "Change", provider);

        builder.Options.TriggerMode.Should().Be(UpdateTriggerMode.ManualInteractive);

        MigrationResult result = await builder.Build().RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Deferred);
        prompt.Asked.Should().BeTrue();
        provider.UpCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_custom_strategy_replaces_the_built_in_pair()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        // The extension point that makes a third installation model - a cloud-backed one, or
        // Unity's IndexedDB on WebGL - a matter of writing one class rather than editing the engine.
        CountingStrategy strategy = new(harness.DataDirectory);

        MigrationResult result = await new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .StartingFromVersion(new Version(1, 0, 0))
            .AddStrategy(strategy)
            .AddStep("2.0.0", "Change", new RecordingProvider("p"))
            .Build()
            .RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        strategy.Prepared.Should().Be(1);
        strategy.Committed.Should().Be(1);
        strategy.RolledBack.Should().Be(0);
    }

    [Fact]
    public void Building_without_a_data_directory_says_so_at_startup()
    {
        Action act = () => new MigrationEngineBuilder().TargetVersion("2.0.0").Build();

        act.Should().Throw<MigrationException>().WithMessage("*DataDirectory has not been set*");
    }

    [Fact]
    public void The_fluent_methods_reject_null()
    {
        MigrationEngineBuilder builder = new();

        ((Action)(() => builder.TargetVersion((Version)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.StartingFromVersion(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.AddStep((IMigrationStep)null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.AddStrategy(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.Configure(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.AskBeforeMigrating(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LogTo_with_a_null_logger_falls_back_to_discarding()
    {
        MigrationEngineBuilder builder = new MigrationEngineBuilder().LogTo((IMigrationLogger)null!);

        builder.Options.Logger.Should().BeSameAs(NullMigrationLogger.Instance);
    }

    private sealed class DecliningPrompt : IUpdatePromptService
    {
        public bool Asked { get; private set; }

        public Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken)
        {
            Asked = true;
            return Task.FromResult(false);
        }
    }

    private sealed class RecordingJournal : IMigrationJournal
    {
        private MigrationJournalEntry? _entry;

        public List<MigrationPhase> Phases { get; } = [];

        public bool Cleared { get; private set; }

        public MigrationJournalEntry? Read() => _entry;

        public void Write(MigrationJournalEntry entry)
        {
            _entry = entry;
            Phases.Add(entry.Phase);
            Cleared = false;
        }

        public void Clear()
        {
            _entry = null;
            Cleared = true;
        }
    }

    private sealed class RecordingLock(bool available) : IMigrationLock
    {
        public int AcquireCount { get; private set; }

        public bool Released { get; private set; }

        public IDisposable? TryAcquire()
        {
            AcquireCount++;
            return available ? new Handle(this) : null;
        }

        private sealed class Handle(RecordingLock owner) : IDisposable
        {
            public void Dispose() => owner.Released = true;
        }
    }

    private sealed class CountingStrategy(string directory) : IInstallationStrategy
    {
        public InstallationModel Model => InstallationModel.InPlaceSingleFolder;

        public int Prepared { get; private set; }

        public int Committed { get; private set; }

        public int RolledBack { get; private set; }

        public DataLocation ResolveCurrentData() =>
            new(directory, new FileDataVersionStore(directory).Read(), exists: true);

        public bool RequiresRunWithEmptyPlan(DataLocation currentData) => false;

        public Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            Prepared++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress)
        {
            Committed++;
            new FileDataVersionStore(context.WorkingDirectory).Write(context.TargetDataVersion, appliedStepIds);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress)
        {
            RolledBack++;
            return Task.CompletedTask;
        }

        public Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress) => Task.CompletedTask;
    }
}
