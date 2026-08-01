// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// The snapshot is the longest phase of a real run, and it is the phase whose progress the user
/// spends the most time looking at. These pin the two things that has to be true of it: reports
/// arrive on the thread doing the work, in the phase that was actually running, and their number
/// is governed by how far the bar has moved rather than by how many files the directory holds.
/// </summary>
public class SnapshotProgressTests
{
    [Fact]
    public async Task Snapshot_progress_is_delivered_before_the_run_moves_on_to_the_steps()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 60);
        harness.StampVersion(new Version(1, 0, 0));

        List<MigrationProgress> reports = [];
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Only", new RecordingProvider("only"))]);

        // A deliberately slow handler. Were these reports posted through a SynchronizationContext
        // instead of relayed inline, the copy would run to completion while the callbacks queued
        // up, and Preparing reports would land after the run had moved into Migrating - rescaled
        // into the wrong slice of the bar on the way.
        await engine.RunAsync(new SlowProgress(reports.Add));

        int firstMigrating = reports.FindIndex(report => report.Phase == MigrationPhase.Migrating);
        int lastPreparing = reports.FindLastIndex(report => report.Phase == MigrationPhase.Preparing);

        firstMigrating.Should().BeGreaterThan(-1, "the run had a step to apply");
        lastPreparing.Should().BeGreaterThan(-1, "there was data to snapshot");
        lastPreparing.Should().BeLessThan(firstMigrating, "the snapshot finishes before the first step starts");
    }

    [Fact]
    public async Task Snapshot_progress_stays_inside_the_share_of_the_bar_that_belongs_to_it()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 60);
        harness.StampVersion(new Version(1, 0, 0));

        List<MigrationProgress> reports = [];
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Only", new RecordingProvider("only"))]);

        await engine.RunAsync(new SlowProgress(reports.Add));

        reports.Where(report => report.Phase == MigrationPhase.Preparing)
            .Select(report => report.Percentage)
            .Should().OnlyContain(percentage => percentage <= 20.0, "preparation owns the first fifth of the bar");

        reports.Select(report => report.Percentage)
            .Should().BeInAscendingOrder("a progress bar that jumps backwards reads as a bug");

        reports[^1].Phase.Should().Be(MigrationPhase.Completed);
        reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task The_number_of_snapshot_reports_tracks_the_bar_rather_than_the_file_count()
    {
        using TestHarness harness = new();
        SeedFiles(harness, count: 1000);
        harness.StampVersion(new Version(1, 0, 0));

        List<MigrationProgress> reports = [];
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Only", new RecordingProvider("only"))]);

        await engine.RunAsync(new SynchronousProgress(reports.Add));

        // A report per file would be a thousand of them, each formatting a string and walking
        // the whole chain out to the UI, for a bar that has nowhere to put them. The copy only
        // reports when the figure has actually moved, so the count is bounded by the bar's own
        // resolution - a few hundred at the very most - however many files the directory holds.
        reports.Count(report => report.Phase == MigrationPhase.Preparing)
            .Should().BeLessThan(250);
    }

    [Fact]
    public async Task A_run_with_no_data_to_copy_still_reports_a_complete_preparation()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        List<MigrationProgress> reports = [];
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Only", new RecordingProvider("only"))]);

        await engine.RunAsync(new SynchronousProgress(reports.Add));

        reports.Should().Contain(report => report.Phase == MigrationPhase.Preparing);
        reports[^1].Percentage.Should().Be(100);
    }

    private static void SeedFiles(TestHarness harness, int count)
    {
        for (int i = 0; i < count; i++)
            harness.WriteFile($"documents/file-{i:D4}.txt", new string('x', 512));
    }

    private sealed class SynchronousProgress(Action<MigrationProgress> onReport) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => onReport(value);
    }

    private sealed class SlowProgress(Action<MigrationProgress> onReport) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value)
        {
            Thread.Sleep(1);
            onReport(value);
        }
    }
}
