// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// The one-file-per-step layout: a step declares itself with <c>[MigrationStep]</c> and is found by
/// scanning, instead of being listed in a registration chain that has to be kept in sync.
/// </summary>
public class StepDiscoveryTests
{
    // A step whose logic is long enough to want a file of its own is the case this exists for;
    // these are deliberately tiny so the discovery mechanics are what the test is about.
    [MigrationStep("1.1.0", "Writes the tag index")]
    public sealed class AddTagIndex : CodeMigrationStep
    {
        public override bool CanDown => true;

        public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            File.WriteAllText(context.GetWorkingPath("tags.idx"), "built");
            return Task.CompletedTask;
        }

        public override Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            File.Delete(context.GetWorkingPath("tags.idx"));
            return Task.CompletedTask;
        }
    }

    [MigrationStep("2.0.0", "Rebuilds the search index", Id = "2.0.0-rebuild-search")]
    public sealed class RebuildSearchIndex : CodeMigrationStep
    {
        public override double Weight => 8.0;

        public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new MigrationProgress(50, "Indexing"));
            File.WriteAllText(context.GetWorkingPath("search.idx"), "built");
            return Task.CompletedTask;
        }
    }

    [MigrationStep("1.5.0", "Composes two providers")]
    public sealed class ComposedStep : MigrationStepBase
    {
        protected override IEnumerable<IMigrationProvider> CreateProviders()
        {
            yield return new RecordingProvider("first");
            yield return new RecordingProvider("second");
        }
    }

    [MigrationStep("9.0.0")]
    public sealed class StepWithoutDescription : CodeMigrationStep
    {
        public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static IReadOnlyList<IMigrationStep> ScanThisFile() =>
        MigrationStepScanner.Scan(Assembly.GetExecutingAssembly(), filter: type => type.DeclaringType == typeof(StepDiscoveryTests));

    [Fact]
    public void Scanning_finds_every_declared_step_ordered_by_version()
    {
        IReadOnlyList<IMigrationStep> steps = ScanThisFile();

        // Reflection returns types in an unspecified order that is not guaranteed stable across
        // builds, so the scanner sorts - "the order steps run in" is not left to chance.
        steps.Select(step => step.TargetVersion.ToString())
            .Should().Equal(["1.1.0", "1.5.0", "2.0.0", "9.0.0"]);
    }

    [Fact]
    public void A_step_takes_its_version_description_and_id_from_the_attribute()
    {
        IMigrationStep step = ScanThisFile().Single(s => s.TargetVersion == new Version(2, 0, 0));

        step.Description.Should().Be("Rebuilds the search index");
        step.Id.Should().Be("2.0.0-rebuild-search", "an explicit id survives renaming the class");
    }

    [Fact]
    public void A_step_with_no_description_falls_back_to_its_class_name()
    {
        IMigrationStep step = ScanThisFile().Single(s => s.TargetVersion == new Version(9, 0, 0));

        step.Description.Should().Be(nameof(StepWithoutDescription));
        step.Id.Should().Be(nameof(StepWithoutDescription));
    }

    [Fact]
    public void A_CodeMigrationStep_is_its_own_single_provider()
    {
        IMigrationStep step = ScanThisFile().Single(s => s.TargetVersion == new Version(2, 0, 0));

        step.Providers.Should().ContainSingle().Which.Should().BeSameAs(step);
        step.Providers[0].Weight.Should().Be(8.0);
        step.Providers[0].Name.Should().Be("Rebuilds the search index");
    }

    [Fact]
    public void A_MigrationStepBase_builds_its_providers_once_and_only_when_asked()
    {
        ComposedStep step = new();

        step.Providers.Should().HaveCount(2);
        step.Providers.Should().BeSameAs(step.Providers, "providers are built once and cached");
    }

    [Fact]
    public void A_class_deriving_from_the_base_without_the_attribute_fails_with_a_useful_message()
    {
        Action act = () => new UndeclaredStep();

        act.Should().Throw<MigrationPlanException>()
            .WithMessage("*has no [MigrationStep] attribute*")
            .WithMessage("*Add [MigrationStep(*");
    }

    [Fact]
    public void A_step_with_no_parameterless_constructor_says_so_instead_of_failing_obscurely()
    {
        Action act = () => MigrationStepScanner.Scan(
            Assembly.GetExecutingAssembly(),
            filter: type => type == typeof(NeedsDependencies));

        act.Should().Throw<MigrationPlanException>()
            .WithMessage("*no public parameterless constructor*")
            .WithMessage("*DependencyInjection*");
    }

    [Fact]
    public void A_custom_factory_can_supply_dependencies()
    {
        IReadOnlyList<IMigrationStep> steps = MigrationStepScanner.Scan(
            Assembly.GetExecutingAssembly(),
            factory: _ => new NeedsDependencies("injected"),
            filter: type => type == typeof(NeedsDependencies));

        steps.Should().ContainSingle();
        ((NeedsDependencies)steps[0]).Dependency.Should().Be("injected");
    }

    [Fact]
    public async Task Discovered_steps_run_through_the_engine_in_version_order()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new MigrationEngineBuilder()
            .UseInPlaceModel()
            .UseDataDirectory(harness.DataDirectory)
            .UseBackupDirectory(harness.BackupRoot)
            .TargetVersion("2.0.0")
            .StartingFromVersion(new Version(1, 0, 0))
            .Configure(options => options.SkipFreeSpaceCheck = true)
            .AddStepsFromAssembly(
                Assembly.GetExecutingAssembly(),
                type => type.DeclaringType == typeof(StepDiscoveryTests) && type != typeof(StepWithoutDescription))
            .Build();

        engine.CreatePlan().Steps.Select(step => step.TargetVersion.ToString())
            .Should().Equal("1.1.0", "1.5.0", "2.0.0");

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.FileExists("tags.idx").Should().BeTrue();
        harness.FileExists("search.idx").Should().BeTrue();
        harness.ReadStampedVersion().Should().Be(new Version(2, 0, 0));
    }

    [Fact]
    public void Two_discovered_steps_reaching_the_same_version_are_rejected_when_the_engine_is_built()
    {
        using TestHarness harness = new();

        Action act = () => new MigrationEngine(
            harness.CreateOptions(),
            [new DuplicateA(), new DuplicateB()]);

        act.Should().Throw<MigrationPlanException>().WithMessage("*Two migration steps target version 3.0.0*");
    }

    [Fact]
    public void The_attribute_rejects_a_version_it_cannot_parse()
    {
        Action act = () => new MigrationStepAttribute("not-a-version");

        act.Should().Throw<ArgumentException>().WithMessage("*is not a valid version*");
    }
}

/// <summary>Deliberately missing its <c>[MigrationStep]</c> attribute.</summary>
public sealed class UndeclaredStep : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>Needs a constructor argument, so it cannot be created by the default factory.</summary>
[MigrationStep("4.0.0", "Needs a dependency")]
public sealed class NeedsDependencies : CodeMigrationStep
{
    public NeedsDependencies(string dependency)
    {
        Dependency = dependency;
    }

    public string Dependency { get; }

    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

[MigrationStep("3.0.0", "A")]
public sealed class DuplicateA : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

[MigrationStep("3.0.0", "B")]
public sealed class DuplicateB : CodeMigrationStep
{
    public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
