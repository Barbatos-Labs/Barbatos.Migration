// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// The applied-step ledger is what an installed copy uses to recognise steps it has already
/// been through, so it accumulates across runs. These pin the two properties that gives it:
/// nothing is ever recorded twice, and the order in which steps ran is preserved.
/// </summary>
public class DataVersionStoreTests
{
    [Fact]
    public void The_version_round_trips()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Write(new Version(2, 1, 0), []);

        store.Read().Should().Be(new Version(2, 1, 0));
    }

    [Fact]
    public void Unstamped_data_reports_no_version_and_no_steps()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Read().Should().BeNull();
        store.ReadAppliedStepIds().Should().BeEmpty();
    }

    [Fact]
    public void Step_ids_accumulate_across_runs_in_the_order_they_ran()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Write(new Version(1, 1, 0), ["1.1.0-a", "1.1.0-b"]);
        store.Write(new Version(1, 2, 0), ["1.2.0-c"]);

        store.ReadAppliedStepIds().Should().Equal("1.1.0-a", "1.1.0-b", "1.2.0-c");
        store.Read().Should().Be(new Version(1, 2, 0));
    }

    [Fact]
    public void A_step_that_is_recorded_again_is_not_duplicated_and_keeps_its_original_position()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Write(new Version(1, 1, 0), ["a", "b", "c"]);

        // The shape a re-run takes: the snapshot was restored, so the whole step runs again.
        store.Write(new Version(1, 1, 0), ["b", "d"]);

        store.ReadAppliedStepIds().Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void Ids_repeated_within_one_call_are_recorded_once()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Write(new Version(1, 0, 0), ["a", "a", "b"]);

        store.ReadAppliedStepIds().Should().Equal("a", "b");
    }

    [Fact]
    public void Ids_are_matched_case_sensitively_because_they_are_identities_not_names()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        store.Write(new Version(1, 0, 0), ["SplitName", "splitname"]);

        store.ReadAppliedStepIds().Should().Equal("SplitName", "splitname");
    }

    [Fact]
    public void A_long_ledger_stays_correct_as_it_grows()
    {
        using TestHarness harness = new();
        FileDataVersionStore store = new(harness.DataDirectory);

        for (int i = 0; i < 200; i++)
            store.Write(new Version(1, 0, i), [$"step-{i}"]);

        IReadOnlyList<string> applied = store.ReadAppliedStepIds();

        applied.Should().HaveCount(200);
        applied[0].Should().Be("step-0");
        applied[199].Should().Be("step-199");
    }

    [Fact]
    public void The_stamp_lives_inside_the_directory_it_describes_so_a_copy_carries_its_version()
    {
        using TestHarness harness = new();
        new FileDataVersionStore(harness.DataDirectory).Write(new Version(3, 0, 0), ["x"]);

        string clone = Path.Combine(harness.Root, "Clone");
        Directory.CreateDirectory(clone);
        File.Copy(
            Path.Combine(harness.DataDirectory, FileDataVersionStore.DefaultFileName),
            Path.Combine(clone, FileDataVersionStore.DefaultFileName));

        new FileDataVersionStore(clone).Read().Should().Be(new Version(3, 0, 0));
        new FileDataVersionStore(clone).ReadAppliedStepIds().Should().Equal("x");
    }
}
