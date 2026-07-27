// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class InPlaceMigrationTests
{
    [Fact]
    public async Task A_successful_run_applies_every_step_and_stamps_the_new_version()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.json", "{ \"v\": 1 }");
        harness.StampVersion(new Version(1, 0, 0));

        RecordingProvider first = new("first", (context, _) =>
        {
            File.WriteAllText(context.GetWorkingPath("settings.json"), "{ \"v\": 2 }");
            return Task.CompletedTask;
        });

        RecordingProvider second = new("second", (context, _) =>
        {
            File.WriteAllText(context.GetWorkingPath("added.txt"), "added at 2.0");
            return Task.CompletedTask;
        });

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(1, 5, 0), "First", first),
                new MigrationStep(new Version(2, 0, 0), "Second", second),
            ]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        result.CanContinue.Should().BeTrue();
        result.AppliedSteps.Select(s => s.Id).Should().Equal("1.5.0", "2.0.0");

        harness.ReadFile("settings.json").Should().Be("{ \"v\": 2 }");
        harness.ReadFile("added.txt").Should().Be("added at 2.0");
        harness.ReadStampedVersion().Should().Be(new Version(2, 0, 0));

        first.UpCallCount.Should().Be(1);
        second.UpCallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_second_run_does_nothing_because_the_version_was_stamped()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        RecordingProvider provider = new("only");
        MigrationEngine engine = new(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Only", provider)]);

        await engine.RunAsync();
        MigrationResult second = await engine.RunAsync();

        second.Outcome.Should().Be(MigrationOutcome.UpToDate);
        provider.UpCallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_step_restores_the_data_exactly_as_it_was()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.WriteFile("nested/deep.txt", "nested original");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(1, 5, 0), "Rewrites data", new RecordingProvider("rewrite", (context, _) =>
                {
                    File.WriteAllText(context.GetWorkingPath("keep.txt"), "CORRUPTED");
                    File.Delete(context.GetWorkingPath("nested/deep.txt"));
                    File.WriteAllText(context.GetWorkingPath("garbage.txt"), "half-migrated junk");
                    return Task.CompletedTask;
                })),
                new MigrationStep(new Version(2, 0, 0), "Explodes", new RecordingProvider("boom", (_, _) =>
                    throw new InvalidOperationException("the disk caught fire"))),
            ]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        result.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("the disk caught fire");
        result.CurrentVersion.Should().Be(new Version(1, 0, 0));

        harness.ReadFile("keep.txt").Should().Be("original");
        harness.ReadFile("nested/deep.txt").Should().Be("nested original");
        harness.FileExists("garbage.txt").Should().BeFalse();
        harness.ReadStampedVersion().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task Cancelling_mid_step_restores_the_data_and_reports_Canceled()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        using CancellationTokenSource cancellation = new();

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(2, 0, 0), "Long running", new RecordingProvider("slow", (context, token) =>
                {
                    File.WriteAllText(context.GetWorkingPath("keep.txt"), "PARTIALLY MIGRATED");

                    // What a real provider's loop does the moment the user clicks Cancel.
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                })),
            ]);

        MigrationResult result = await engine.RunAsync(cancellationToken: cancellation.Token);

        result.Outcome.Should().Be(MigrationOutcome.Canceled);
        result.Error.Should().BeNull("a user cancelling is not a failure");
        harness.ReadFile("keep.txt").Should().Be("original");
        harness.ReadStampedVersion().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task A_run_killed_before_it_finished_is_recovered_on_the_next_launch()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        // Simulate a process killed mid-migration: a complete snapshot, a journal saying the
        // run was in the Migrating phase, and a data directory that has been half-rewritten.
        string snapshot = Path.Combine(harness.BackupRoot, "snapshot-session1");
        Directory.CreateDirectory(snapshot);
        foreach (string file in Directory.GetFiles(harness.DataDirectory))
            File.Copy(file, Path.Combine(snapshot, Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(harness.DataDirectory, "keep.txt"), "HALF MIGRATED");
        File.WriteAllText(Path.Combine(harness.DataDirectory, "orphan.txt"), "left behind by the dead run");

        new FileMigrationJournal(harness.BackupRoot).Write(new MigrationJournalEntry(
            "session1",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            InstallationModel.InPlaceSingleFolder,
            MigrationDirection.Upgrade,
            new Version(1, 0, 0),
            new Version(2, 0, 0),
            harness.DataDirectory,
            harness.DataDirectory,
            snapshot,
            MigrationPhase.Migrating,
            lastCompletedStepId: null));

        RecordingProvider provider = new("retry", (context, _) =>
        {
            File.WriteAllText(context.GetWorkingPath("keep.txt"), "migrated properly");
            return Task.CompletedTask;
        });

        MigrationEngine engine = new(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Retry", provider)]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        // The interrupted run's leftovers are gone and the retry started from the restored
        // snapshot, not from the half-migrated state.
        harness.FileExists("orphan.txt").Should().BeFalse();
        harness.ReadFile("keep.txt").Should().Be("migrated properly");
        harness.LogMessages.Should().Contain(message => message.Contains("did not finish"));
    }

    [Fact]
    public async Task A_run_killed_during_preparation_leaves_the_data_untouched()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        // Preparing means no provider had run yet: the partial snapshot must be discarded, and
        // restoring from it would be actively wrong.
        string snapshot = Path.Combine(harness.BackupRoot, "snapshot-session2");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "keep.txt"), "INCOMPLETE SNAPSHOT");

        new FileMigrationJournal(harness.BackupRoot).Write(new MigrationJournalEntry(
            "session2", DateTimeOffset.UtcNow, InstallationModel.InPlaceSingleFolder, MigrationDirection.Upgrade,
            new Version(1, 0, 0), new Version(2, 0, 0), harness.DataDirectory, harness.DataDirectory, snapshot,
            MigrationPhase.Preparing, null));

        MigrationEngine engine = new(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Retry", new RecordingProvider("noop"))]);

        await engine.RunAsync();

        harness.ReadFile("keep.txt").Should().Be("original");
        Directory.Exists(snapshot).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_process_is_blocked_while_a_migration_is_running()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationOptions options = harness.CreateOptions();
        FileMigrationLock held = new(harness.BackupRoot);
        Directory.CreateDirectory(harness.BackupRoot);

        using IDisposable? handle = held.TryAcquire();
        handle.Should().NotBeNull("the first process takes the lock");

        MigrationEngine engine = new(options, [new MigrationStep(new Version(2, 0, 0), "Blocked", new RecordingProvider("never"))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Blocked);
        result.Error.Should().BeOfType<MigrationLockException>();
        result.CanContinue.Should().BeFalse();
    }

    [Fact]
    public async Task Data_newer_than_the_application_is_blocked_rather_than_silently_accepted()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(3, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Older", new RecordingProvider("p"))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Blocked);
        result.Error!.Message.Should().Contain("newer than this build");
    }

    [Fact]
    public async Task A_downgrade_runs_the_steps_backwards_when_it_is_allowed()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(2, 0, 0));

        List<string> order = [];
        RecordingProvider oneFive = new("1.5", (_, _) => { order.Add("1.5"); return Task.CompletedTask; }, canDown: true);
        RecordingProvider two = new("2.0", (_, _) => { order.Add("2.0"); return Task.CompletedTask; }, canDown: true);

        MigrationEngine engine = new(
            harness.CreateOptions(options =>
            {
                options.TargetDataVersion = new Version(1, 0, 0);
                options.AllowDowngrade = true;
            }),
            [
                new MigrationStep(new Version(1, 5, 0), "To 1.5", oneFive),
                new MigrationStep(new Version(2, 0, 0), "To 2.0", two),
            ]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        order.Should().Equal("2.0", "1.5");
        two.DownCallCount.Should().Be(1);
        oneFive.DownCallCount.Should().Be(1);
        harness.ReadStampedVersion().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task The_snapshot_is_kept_when_retention_asks_for_it_and_deleted_when_it_does_not()
    {
        using TestHarness harness = new();
        harness.WriteFile("keep.txt", "original");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(options => options.BackupRetentionCount = 1),
            [new MigrationStep(new Version(2, 0, 0), "Change", new RecordingProvider("p", (context, _) =>
            {
                File.WriteAllText(context.GetWorkingPath("keep.txt"), "new");
                return Task.CompletedTask;
            }))]);

        MigrationResult result = await engine.RunAsync();

        result.BackupDirectory.Should().NotBeNull();
        Directory.Exists(result.BackupDirectory!).Should().BeTrue();
        File.ReadAllText(Path.Combine(result.BackupDirectory!, "keep.txt")).Should().Be("original");
    }

    [Fact]
    public async Task Progress_is_reported_monotonically_and_ends_at_100()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        List<MigrationProgress> reports = [];

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(1, 5, 0), "One", new RecordingProvider("a"), new RecordingProvider("b")),
                new MigrationStep(new Version(2, 0, 0), "Two", new RecordingProvider("c")),
            ]);

        await engine.RunAsync(new SynchronousProgress(reports.Add));

        reports.Should().NotBeEmpty();
        reports.Select(r => r.Percentage).Should().BeInAscendingOrder("a progress bar that jumps backwards reads as a bug");
        reports[^1].Percentage.Should().Be(100);
        reports[^1].Phase.Should().Be(MigrationPhase.Completed);
    }

    [Fact]
    public void A_backup_directory_nested_inside_the_data_directory_is_rejected()
    {
        using TestHarness harness = new();

        Action act = () => new MigrationEngine(
            harness.CreateOptions(options => options.BackupRootDirectory = Path.Combine(harness.DataDirectory, "backups")),
            []);

        act.Should().Throw<MigrationException>().WithMessage("*would then contain itself*");
    }

    private sealed class SynchronousProgress(Action<MigrationProgress> onReport) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => onReport(value);
    }
}
