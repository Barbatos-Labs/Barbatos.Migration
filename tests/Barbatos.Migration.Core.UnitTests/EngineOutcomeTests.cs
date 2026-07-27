// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// The outcomes an application has to branch on, and the difference between "it worked" and
/// "you may carry on" - which are not the same question.
/// </summary>
public class EngineOutcomeTests
{
    private sealed class StubPrompt(bool answer) : IUpdatePromptService
    {
        public MigrationPromptContext? Received { get; private set; }

        public int CallCount { get; private set; }

        public Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken)
        {
            Received = context;
            CallCount++;
            return Task.FromResult(answer);
        }
    }

    /// <summary>A strategy whose rollback fails - the one case where user data may be damaged.</summary>
    private sealed class BrokenRollbackStrategy(MigrationOptions options) : IInstallationStrategy
    {
        public InstallationModel Model => InstallationModel.InPlaceSingleFolder;

        public DataLocation ResolveCurrentData() =>
            new(options.DataDirectory, new FileDataVersionStore(options.DataDirectory).Read(), exists: true);

        public bool RequiresRunWithEmptyPlan(DataLocation currentData) => false;

        public Task PrepareAsync(MigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            string snapshot = Path.Combine(options.BackupRootDirectory, "snapshot-" + context.SessionId);
            Directory.CreateDirectory(snapshot);
            context.SetBackupDirectory(snapshot);
            return Task.CompletedTask;
        }

        public Task CommitAsync(MigrationContext context, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>? progress) =>
            Task.CompletedTask;

        public Task RollbackAsync(MigrationContext context, Exception? error, IProgress<MigrationProgress>? progress) =>
            throw new IOException("the backup volume went away");

        public Task RecoverAsync(MigrationJournalEntry journal, IProgress<MigrationProgress>? progress) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task ManualInteractive_asks_before_touching_anything_and_Deferred_means_nothing_changed()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        StubPrompt prompt = new(answer: false);
        RecordingProvider provider = new("never runs");

        MigrationEngine engine = new(
            harness.CreateOptions(options => options.TriggerMode = UpdateTriggerMode.ManualInteractive),
            [new MigrationStep(new Version(2, 0, 0), "Big change", provider)],
            promptService: prompt);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Deferred);
        result.CurrentVersion.Should().Be(new Version(1, 0, 0));
        provider.UpCallCount.Should().Be(0);
        harness.ReadFile("keep.txt").Should().Be("original");

        // The prompt is asked before the snapshot, so declining costs nothing.
        Directory.Exists(harness.BackupRoot).Should().BeTrue("the backup root is created for the lock");
        Directory.EnumerateDirectories(harness.BackupRoot).Should().BeEmpty("no snapshot was taken");
    }

    [Fact]
    public async Task The_prompt_receives_the_plan_the_model_and_whether_deferring_is_a_real_option()
    {
        using TestHarness harness = new();
        harness.WriteFile("data.bin", new string('x', 2048));
        harness.StampVersion(new Version(1, 0, 0));

        StubPrompt prompt = new(answer: true);

        MigrationEngine engine = new(
            harness.CreateOptions(options =>
            {
                options.TriggerMode = UpdateTriggerMode.ManualInteractive;
                options.AllowRunningOnOlderData = true;
            }),
            [
                new MigrationStep(new Version(1, 5, 0), "First change", new RecordingProvider("a")),
                new MigrationStep(new Version(2, 0, 0), "Second change", new RecordingProvider("b")),
            ],
            promptService: prompt);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        prompt.CallCount.Should().Be(1);

        MigrationPromptContext received = prompt.Received!;
        received.Plan.Steps.Should().HaveCount(2);
        received.Plan.Describe().Should().Contain("First change").And.Contain("Second change");
        received.Model.Should().Be(InstallationModel.InPlaceSingleFolder);
        received.CanDefer.Should().BeTrue("AllowRunningOnOlderData says the app can run without this");
        received.EstimatedDataSizeBytes.Should().BeGreaterThan(2000, "so the prompt can say how long it will take");
    }

    [Fact]
    public async Task CanDefer_is_false_when_the_application_cannot_run_on_older_data()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        StubPrompt prompt = new(answer: false);

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(options =>
            {
                options.TriggerMode = UpdateTriggerMode.ManualInteractive;
                options.AllowRunningOnOlderData = false;
            }),
            [new MigrationStep(new Version(2, 0, 0), "Required", new RecordingProvider("p"))],
            promptService: prompt).RunAsync();

        prompt.Received!.CanDefer.Should().BeFalse(
            "a dialog must not offer a 'Remind me later' that leads nowhere");

        result.Outcome.Should().Be(MigrationOutcome.Deferred);
        result.CanContinue.Should().BeFalse("the app has declared it cannot run against the old data");
    }

    [Fact]
    public async Task SilentAutoUpdate_never_consults_the_prompt()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        StubPrompt prompt = new(answer: false);

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Silent", new RecordingProvider("p"))],
            promptService: prompt).RunAsync();

        prompt.CallCount.Should().Be(0);
        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
    }

    [Fact]
    public async Task A_failed_rollback_is_reported_separately_and_keeps_the_snapshot()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationOptions options = harness.CreateOptions();

        MigrationResult result = await new MigrationEngine(
            options,
            [new MigrationStep(new Version(2, 0, 0), "Explodes", new RecordingProvider("boom", (_, _) =>
                throw new InvalidOperationException("the step failed")))],
            strategies: [new BrokenRollbackStrategy(options)]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.RollbackFailed);
        result.CanContinue.Should().BeFalse("this is the one outcome where user data may be damaged");
        result.Error.Should().BeOfType<InvalidOperationException>();
        result.RollbackError.Should().BeOfType<IOException>();

        // The snapshot is deliberately left behind - it is now the user's only intact copy -
        // and so is the journal, so the next launch tries the recovery again.
        result.BackupDirectory.Should().NotBeNull();
        Directory.Exists(result.BackupDirectory!).Should().BeTrue();
        File.Exists(new FileMigrationJournal(harness.BackupRoot).FilePath).Should().BeTrue();

        harness.LogMessages.Should().Contain(message =>
            message.StartsWith("Critical") && message.Contains("THE ROLLBACK FAILED"));
    }

    [Fact]
    public async Task AllowRunningOnOlderData_decides_whether_a_clean_rollback_lets_the_app_start()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationStep failing = new(new Version(2, 0, 0), "Explodes",
            new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("nope")));

        MigrationResult strict = await new MigrationEngine(harness.CreateOptions(), [failing]).RunAsync();
        strict.Outcome.Should().Be(MigrationOutcome.Failed);
        strict.CanContinue.Should().BeFalse();

        MigrationResult lenient = await new MigrationEngine(
            harness.CreateOptions(options => options.AllowRunningOnOlderData = true), [failing]).RunAsync();

        lenient.Outcome.Should().Be(MigrationOutcome.Failed);
        lenient.CanContinue.Should().BeTrue("the data is intact, just at the old version");
        lenient.IsSuccess.Should().BeFalse("IsSuccess and CanContinue answer different questions");
    }

    [Fact]
    public async Task A_result_reports_the_steps_that_ran_and_how_long_it_took()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(1, 5, 0), "First", new RecordingProvider("a")),
                new MigrationStep(new Version(2, 0, 0), "Second", new RecordingProvider("b")),
            ]).RunAsync();

        result.AppliedSteps.Select(step => step.Description).Should().Equal(["First", "Second"]);
        result.AppliedSteps[0].TargetVersion.Should().Be(new Version(1, 5, 0));
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.WorkingDirectory.Should().Be(harness.DataDirectory);
        result.ToString().Should().Contain("Succeeded").And.Contain("1.0.0 -> 2.0.0");
    }

    [Fact]
    public async Task The_ledger_accumulates_every_step_a_copy_of_the_data_has_been_through()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationStep first = new(new Version(1, 5, 0), "First", [new RecordingProvider("a")], id: "1.5.0-first");
        MigrationStep second = new(new Version(2, 0, 0), "Second", [new RecordingProvider("b")], id: "2.0.0-second");

        await new MigrationEngine(
            harness.CreateOptions(options => options.TargetDataVersion = new Version(1, 5, 0)), [first]).RunAsync();

        await new MigrationEngine(harness.CreateOptions(), [first, second]).RunAsync();

        new FileDataVersionStore(harness.DataDirectory).ReadAppliedStepIds()
            .Should().Equal(["1.5.0-first", "2.0.0-second"]);
    }

    [Fact]
    public async Task A_custom_data_version_store_replaces_the_stamp_file_entirely()
    {
        using TestHarness harness = new();
        InMemoryVersionStore store = new(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(options => options.DataVersionStoreFactory = _ => store),
            [new MigrationStep(new Version(2, 0, 0), "Change", new RecordingProvider("p"))]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        store.Version.Should().Be(new Version(2, 0, 0));
        File.Exists(Path.Combine(harness.DataDirectory, FileDataVersionStore.DefaultFileName))
            .Should().BeFalse("the replacement store owns the version, so no stamp file is written");
    }

    private sealed class InMemoryVersionStore(Version? initial) : IDataVersionStore
    {
        private readonly List<string> _steps = [];

        public Version? Version { get; private set; } = initial;

        public Version? Read() => Version;

        public IReadOnlyList<string> ReadAppliedStepIds() => _steps;

        public void Write(Version version, IReadOnlyList<string> appliedStepIds)
        {
            Version = version;
            _steps.AddRange(appliedStepIds);
        }
    }
}
