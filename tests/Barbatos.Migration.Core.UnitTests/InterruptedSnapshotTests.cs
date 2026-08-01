// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// What happens when the snapshot itself does not finish.
/// </summary>
/// <remarks>
/// This is the interruption users actually produce: a long copy, a Cancel button, a laptop lid.
/// Nothing has written to the data directory at that point - no provider has run - so the whole
/// of the correct response is to leave it alone. The trap is that a partially written snapshot
/// looks exactly like a complete one to anything that only checks whether the directory exists,
/// and "restoring" it would replace intact data with a fragment of itself: the safety mechanism
/// destroying the data it exists to protect.
/// </remarks>
public class InterruptedSnapshotTests
{
    [Fact]
    public async Task Cancelling_during_the_snapshot_leaves_every_file_untouched()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 200);
        harness.StampVersion(new Version(1, 0, 0));

        string[] before = SnapshotOfDataDirectory(harness);

        using CancellationTokenSource cancellation = new();
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Never reached", new RecordingProvider("provider"))]);

        MigrationResult result = await engine.RunAsync(
            new CallbackProgress(report =>
            {
                if (report.Phase == MigrationPhase.Preparing && report.Percentage > 2)
                    cancellation.Cancel();
            }),
            cancellation.Token);

        result.Outcome.Should().Be(MigrationOutcome.Canceled);

        SnapshotOfDataDirectory(harness).Should().Equal(
            before,
            "cancelling before any provider ran must leave the data exactly as it was");

        harness.ReadStampedVersion().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task A_cancelled_snapshot_reports_that_there_was_nothing_to_restore()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 200);
        harness.StampVersion(new Version(1, 0, 0));

        using CancellationTokenSource cancellation = new();
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Never reached", new RecordingProvider("provider"))]);

        MigrationResult result = await engine.RunAsync(
            new CallbackProgress(report =>
            {
                if (report.Phase == MigrationPhase.Preparing && report.Percentage > 2)
                    cancellation.Cancel();
            }),
            cancellation.Token);

        // The engine must not offer a fragment as a backup the user could restore by hand.
        result.BackupDirectory.Should().BeNull();
        harness.LogMessages.Should().Contain(message => message.Contains("Nothing to roll back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_fragment_left_by_a_cancelled_snapshot_is_cleaned_up()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 200);
        harness.StampVersion(new Version(1, 0, 0));

        using CancellationTokenSource cancellation = new();
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Never reached", new RecordingProvider("provider"))]);

        await engine.RunAsync(
            new CallbackProgress(report =>
            {
                if (report.Phase == MigrationPhase.Preparing && report.Percentage > 2)
                    cancellation.Cancel();
            }),
            cancellation.Token);

        Directory.EnumerateDirectories(harness.BackupRoot, "snapshot-*")
            .Should().BeEmpty("a half-copied snapshot is of no use to anyone and must not sit on the disk");
    }

    [Fact]
    public async Task A_run_cancelled_during_the_snapshot_can_simply_be_run_again()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 200);
        harness.StampVersion(new Version(1, 0, 0));

        using CancellationTokenSource cancellation = new();
        RecordingProvider provider = new("provider", (context, _) =>
        {
            File.WriteAllText(context.GetWorkingPath("migrated.txt"), "done");
            return Task.CompletedTask;
        });

        MigrationOptions options = harness.CreateOptions();
        MigrationEngine engine = new(options, [new MigrationStep(new Version(2, 0, 0), "Marker", provider)]);

        await engine.RunAsync(
            new CallbackProgress(report =>
            {
                if (report.Phase == MigrationPhase.Preparing && report.Percentage > 2)
                    cancellation.Cancel();
            }),
            cancellation.Token);

        MigrationResult second = await engine.RunAsync();

        second.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("migrated.txt").Should().Be("done");
        harness.ReadStampedVersion().Should().Be(new Version(2, 0, 0));
    }

    [Fact]
    public async Task A_step_that_fails_after_a_complete_snapshot_still_restores_the_data()
    {
        // The counterpart: once the snapshot is complete it must be used. This is the path the
        // null BackupDirectory above must not have broken.
        using TestHarness harness = new();
        harness.WriteFile("settings.json", "original");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [
                new MigrationStep(new Version(2, 0, 0), "Damages then fails", new RecordingProvider("provider", (context, _) =>
                {
                    File.WriteAllText(context.GetWorkingPath("settings.json"), "half migrated");
                    File.Delete(context.GetWorkingPath("settings.json"));
                    throw new InvalidOperationException("step failed");
                })),
            ]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        harness.ReadFile("settings.json").Should().Be("original");
        harness.ReadStampedVersion().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task A_side_by_side_preparation_that_fails_leaves_the_previous_version_intact()
    {
        // The same requirement for the other installation model. Its cleanup deletes the
        // directory it was migrating into, and until the clone exists that name is still the
        // previous version's - the one directory this model exists to never write to.
        using TestHarness harness = new();

        string versionRoot = Path.Combine(harness.Root, "Versions");
        string oldDirectory = Path.Combine(versionRoot, "1.0.0");
        Directory.CreateDirectory(oldDirectory);
        File.WriteAllText(Path.Combine(oldDirectory, "data.txt"), "v1 data");
        new FileDataVersionStore(oldDirectory).Write(new Version(1, 0, 0), []);

        MigrationOptions options = new()
        {
            Model = InstallationModel.SideBySideMultiFolder,
            DataDirectory = versionRoot,
            TargetDataVersion = new Version(2, 0, 0),
            InitialDataVersion = new Version(1, 0, 0),
            Logger = harness.Logger,

            // Refuses the clone before it starts, which is the cheapest way to make the
            // preparation fail without corrupting anything to do it.
            SkipFreeSpaceCheck = false,
            RequiredFreeSpaceFactor = double.MaxValue,
        };

        MigrationEngine engine = new(
            options,
            [new MigrationStep(new Version(2, 0, 0), "Never reached", new RecordingProvider("provider"))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        Directory.Exists(oldDirectory).Should().BeTrue("the previous version is the user's only copy");
        File.ReadAllText(Path.Combine(oldDirectory, "data.txt")).Should().Be("v1 data");
        new FileDataVersionStore(oldDirectory).Read().Should().Be(new Version(1, 0, 0));
    }

    private static void SeedFiles(TestHarness harness, int count)
    {
        for (int i = 0; i < count; i++)
            harness.WriteFile($"documents/file-{i:D4}.txt", new string('x', 1024));
    }

    private static string[] SnapshotOfDataDirectory(TestHarness harness) =>
        [.. Directory.EnumerateFiles(harness.DataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(harness.DataDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)];

    private sealed class CallbackProgress(Action<MigrationProgress> onReport) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => onReport(value);
    }
}
