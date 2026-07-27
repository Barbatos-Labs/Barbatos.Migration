// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class SideBySideMigrationTests
{
    private static MigrationOptions Options(TestHarness harness, Action<MigrationOptions>? configure = null)
    {
        MigrationOptions options = new()
        {
            Model = InstallationModel.SideBySideMultiFolder,
            DataDirectory = Path.Combine(harness.Root, "Versions"),
            TargetDataVersion = new Version(2, 0, 0),
            InitialDataVersion = new Version(1, 0, 0),
            Logger = harness.Logger,
            SkipFreeSpaceCheck = true,
        };

        configure?.Invoke(options);
        return options;
    }

    private static string SeedVersion(TestHarness harness, string version, string fileName, string content)
    {
        string directory = Path.Combine(harness.Root, "Versions", version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        new FileDataVersionStore(directory).Write(Version.Parse(version), []);
        return directory;
    }

    [Fact]
    public async Task An_upgrade_clones_the_previous_version_and_never_touches_it()
    {
        using TestHarness harness = new();
        string oldDirectory = SeedVersion(harness, "1.0.0", "data.txt", "v1 data");

        MigrationEngine engine = new(
            Options(harness),
            [new MigrationStep(new Version(2, 0, 0), "Upgrade", new RecordingProvider("p", (context, _) =>
            {
                File.WriteAllText(context.GetWorkingPath("data.txt"), "v2 data");
                return Task.CompletedTask;
            }))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        string newDirectory = Path.Combine(harness.Root, "Versions", "2.0.0");
        result.WorkingDirectory.Should().Be(newDirectory);

        File.ReadAllText(Path.Combine(newDirectory, "data.txt")).Should().Be("v2 data");
        new FileDataVersionStore(newDirectory).Read().Should().Be(new Version(2, 0, 0));

        // The whole point of the model: the old build still finds its data exactly as it left it.
        File.ReadAllText(Path.Combine(oldDirectory, "data.txt")).Should().Be("v1 data");
        new FileDataVersionStore(oldDirectory).Read().Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public async Task A_failure_leaves_no_new_version_directory_behind()
    {
        using TestHarness harness = new();
        string oldDirectory = SeedVersion(harness, "1.0.0", "data.txt", "v1 data");

        MigrationEngine engine = new(
            Options(harness),
            [new MigrationStep(new Version(2, 0, 0), "Fails", new RecordingProvider("boom", (context, _) =>
            {
                File.WriteAllText(context.GetWorkingPath("data.txt"), "half migrated");
                throw new InvalidOperationException("nope");
            }))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);

        // A version directory that exists is a version directory the next launch will trust, so
        // a failed upgrade must not create one at all.
        Directory.Exists(Path.Combine(harness.Root, "Versions", "2.0.0")).Should().BeFalse();
        File.ReadAllText(Path.Combine(oldDirectory, "data.txt")).Should().Be("v1 data");
    }

    [Fact]
    public async Task The_newest_installed_version_below_the_target_is_the_one_cloned()
    {
        using TestHarness harness = new();
        SeedVersion(harness, "1.0.0", "data.txt", "v1");
        SeedVersion(harness, "1.5.0", "data.txt", "v1.5");

        MigrationEngine engine = new(
            Options(harness),
            [new MigrationStep(new Version(2, 0, 0), "Upgrade", new RecordingProvider("p"))]);

        MigrationResult result = await engine.RunAsync();

        result.FromVersion.Should().Be(new Version(1, 5, 0));
        File.ReadAllText(Path.Combine(harness.Root, "Versions", "2.0.0", "data.txt")).Should().Be("v1.5");
    }

    [Fact]
    public async Task A_fresh_install_creates_and_stamps_the_target_directory()
    {
        using TestHarness harness = new();

        MigrationEngine engine = new(
            Options(harness, options => options.InitialDataVersion = new Version(2, 0, 0)),
            [new MigrationStep(new Version(2, 0, 0), "Never runs", new RecordingProvider("p"))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        string directory = Path.Combine(harness.Root, "Versions", "2.0.0");
        Directory.Exists(directory).Should().BeTrue();
        new FileDataVersionStore(directory).Read().Should().Be(new Version(2, 0, 0));
    }

    [Fact]
    public async Task A_version_with_no_steps_between_it_and_the_previous_one_still_gets_its_data()
    {
        using TestHarness harness = new();
        SeedVersion(harness, "1.0.0", "data.txt", "v1 data");

        // Shipping 2.0 with no schema change must not leave 2.0 with an empty data directory.
        MigrationEngine engine = new(Options(harness), []);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        File.ReadAllText(Path.Combine(harness.Root, "Versions", "2.0.0", "data.txt")).Should().Be("v1 data");
    }

    [Fact]
    public async Task An_already_migrated_target_directory_is_left_alone()
    {
        using TestHarness harness = new();
        SeedVersion(harness, "1.0.0", "data.txt", "v1 data");
        SeedVersion(harness, "2.0.0", "data.txt", "v2 data, already migrated");

        RecordingProvider provider = new("p");
        MigrationEngine engine = new(Options(harness), [new MigrationStep(new Version(2, 0, 0), "Upgrade", provider)]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.UpToDate);
        provider.UpCallCount.Should().Be(0);
        File.ReadAllText(Path.Combine(harness.Root, "Versions", "2.0.0", "data.txt")).Should().Be("v2 data, already migrated");
    }

    [Fact]
    public async Task An_abandoned_staging_clone_is_swept_up_on_the_next_launch()
    {
        using TestHarness harness = new();
        SeedVersion(harness, "1.0.0", "data.txt", "v1 data");

        string backupRoot = Path.Combine(harness.Root, "Versions", ".migration");
        string staging = Path.Combine(backupRoot, "staging-deadsession");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "data.txt"), "abandoned");

        new FileMigrationJournal(backupRoot).Write(new MigrationJournalEntry(
            "deadsession", DateTimeOffset.UtcNow.AddHours(-1), InstallationModel.SideBySideMultiFolder,
            MigrationDirection.Upgrade, new Version(1, 0, 0), new Version(2, 0, 0),
            Path.Combine(harness.Root, "Versions", "1.0.0"), staging, null, MigrationPhase.Migrating, null));

        MigrationEngine engine = new(
            Options(harness),
            [new MigrationStep(new Version(2, 0, 0), "Upgrade", new RecordingProvider("p"))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        Directory.Exists(staging).Should().BeFalse();
    }
}
