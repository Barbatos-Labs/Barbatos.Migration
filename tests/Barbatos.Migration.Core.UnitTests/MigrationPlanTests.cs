// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class MigrationPlanTests
{
    private static MigrationStep Step(string version, bool canDown = false) =>
        new(Version.Parse(version), $"Step {version}", new RecordingProvider($"p{version}", canDown: canDown));

    [Fact]
    public void Create_orders_steps_ascending_for_an_upgrade()
    {
        MigrationPlan plan = MigrationPlan.Create(
            [Step("1.3.0"), Step("1.1.0"), Step("1.2.0")],
            new Version(1, 0, 0),
            new Version(1, 3, 0));

        plan.Direction.Should().Be(MigrationDirection.Upgrade);
        plan.Steps.Select(s => s.TargetVersion.ToString()).Should().Equal("1.1.0", "1.2.0", "1.3.0");
    }

    [Fact]
    public void Create_excludes_steps_at_or_below_the_current_version()
    {
        MigrationPlan plan = MigrationPlan.Create(
            [Step("1.1.0"), Step("1.2.0"), Step("1.3.0")],
            new Version(1, 2, 0),
            new Version(1, 3, 0));

        plan.Steps.Should().ContainSingle().Which.TargetVersion.Should().Be(new Version(1, 3, 0));
    }

    [Fact]
    public void Create_excludes_steps_beyond_the_target_version()
    {
        MigrationPlan plan = MigrationPlan.Create(
            [Step("1.1.0"), Step("1.2.0"), Step("2.0.0")],
            new Version(1, 0, 0),
            new Version(1, 2, 0));

        plan.Steps.Select(s => s.TargetVersion.ToString()).Should().Equal("1.1.0", "1.2.0");
    }

    [Fact]
    public void Create_aggregates_every_skipped_version_into_one_plan()
    {
        // The conservative user who sat on 1.0 through four releases and only upgrades when 2.0
        // ships: all four steps have to run, in order, inside a single protected run.
        MigrationPlan plan = MigrationPlan.Create(
            [Step("1.1.0"), Step("1.2.0"), Step("1.3.0"), Step("2.0.0")],
            new Version(1, 0, 0),
            new Version(2, 0, 0));

        plan.HopCount.Should().Be(4);
        plan.Steps.Select(s => s.TargetVersion.ToString()).Should().Equal("1.1.0", "1.2.0", "1.3.0", "2.0.0");
    }

    [Fact]
    public void Create_returns_an_empty_plan_when_the_versions_match()
    {
        MigrationPlan plan = MigrationPlan.Create([Step("1.1.0")], new Version(1, 1, 0), new Version(1, 1, 0));

        plan.IsEmpty.Should().BeTrue();
        plan.Describe().Should().Contain("nothing to do");
    }

    [Fact]
    public void Create_orders_steps_descending_for_a_downgrade()
    {
        MigrationPlan plan = MigrationPlan.Create(
            [Step("1.1.0", canDown: true), Step("1.2.0", canDown: true), Step("1.3.0", canDown: true)],
            new Version(1, 3, 0),
            new Version(1, 1, 0));

        plan.Direction.Should().Be(MigrationDirection.Downgrade);
        plan.Steps.Select(s => s.TargetVersion.ToString()).Should().Equal("1.3.0", "1.2.0");
    }

    [Fact]
    public void Create_rejects_a_downgrade_across_a_forward_only_provider()
    {
        Action act = () => MigrationPlan.Create(
            [Step("1.1.0", canDown: true), Step("1.2.0", canDown: false)],
            new Version(1, 2, 0),
            new Version(1, 0, 0));

        act.Should().Throw<MigrationPlanException>()
            .WithMessage("*forward-only*")
            .WithMessage("*1.2.0/p1.2.0*");
    }

    [Fact]
    public void Create_rejects_two_steps_targeting_the_same_version()
    {
        Action act = () => MigrationPlan.Create(
            [Step("1.1.0"), Step("1.1.0")],
            new Version(1, 0, 0),
            new Version(1, 1, 0));

        act.Should().Throw<MigrationPlanException>().WithMessage("*Two migration steps target version 1.1.0*");
    }

    [Fact]
    public void Create_rejects_two_steps_sharing_an_id()
    {
        MigrationStep first = new(new Version(1, 1, 0), "a", [new RecordingProvider("p")], id: "shared");
        MigrationStep second = new(new Version(1, 2, 0), "b", [new RecordingProvider("p")], id: "shared");

        Action act = () => MigrationPlan.Create([first, second], new Version(1, 0, 0), new Version(1, 2, 0));

        act.Should().Throw<MigrationPlanException>().WithMessage("*share the id 'shared'*");
    }

    [Fact]
    public void Step_rejects_an_empty_provider_list()
    {
        Action act = () => new MigrationStep(new Version(1, 0, 0), "empty", []);

        act.Should().Throw<ArgumentException>().WithMessage("*has no providers*");
    }

    [Fact]
    public void Describe_lists_every_step_and_provider()
    {
        MigrationPlan plan = MigrationPlan.Create([Step("1.1.0"), Step("1.2.0")], new Version(1, 0, 0), new Version(1, 2, 0));

        plan.Describe().Should()
            .Contain("Upgrade 1.0.0 -> 1.2.0 (2 steps)")
            .And.Contain("1.1.0 - Step 1.1.0 [p1.1.0]");
    }
}
