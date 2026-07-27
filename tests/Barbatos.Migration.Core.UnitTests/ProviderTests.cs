// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.IO;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Barbatos.Migration.FileSystem;
using Barbatos.Migration.Json;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class ProviderTests
{
    [Fact]
    public async Task Json_provider_rewrites_the_document_and_keeps_unknown_keys()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.json", """{ "theme": "dark", "pluginSetting": 42 }""");
        harness.StampVersion(new Version(1, 0, 0));

        JsonMigrationProvider provider = new(
            "settings.json",
            json => json.RenameProperty("theme", "appearance").SetDefault("language", "vi"));

        MigrationEngine engine = new(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Settings", provider)]);

        MigrationResult result = await engine.RunAsync();
        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        JsonObject json = (JsonObject)JsonNode.Parse(harness.ReadFile("settings.json"))!;
        json["appearance"]!.GetValue<string>().Should().Be("dark");
        json["language"]!.GetValue<string>().Should().Be("vi");
        json.ContainsKey("theme").Should().BeFalse();

        // A key this migration knows nothing about - written by a plugin, say - survives.
        json["pluginSetting"]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public async Task Json_provider_leaves_an_existing_user_value_alone()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.json", """{ "language": "en" }""");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Defaults", new JsonMigrationProvider("settings.json", json => json.SetDefault("language", "vi")))]);

        await engine.RunAsync();

        ((JsonObject)JsonNode.Parse(harness.ReadFile("settings.json"))!)["language"]!.GetValue<string>().Should().Be("en");
    }

    [Fact]
    public async Task Json_provider_fails_the_run_on_a_corrupt_file_rather_than_overwriting_it()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.json", "{ this is not json");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Settings", new JsonMigrationProvider("settings.json", json => json.SetDefault("x", 1)))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        result.Error!.Message.Should().Contain("not valid JSON");
        harness.ReadFile("settings.json").Should().Be("{ this is not json");
    }

    [Fact]
    public void Json_section_helpers_move_properties_both_ways()
    {
        JsonObject json = new() { ["fontSize"] = 14 };

        json.MoveIntoSection("fontSize", "editor");
        json["editor"]!["fontSize"]!.GetValue<int>().Should().Be(14);
        json.ContainsKey("fontSize").Should().BeFalse();

        json.MoveOutOfSection("editor", "fontSize");
        json["fontSize"]!.GetValue<int>().Should().Be(14);
        json.ContainsKey("editor").Should().BeFalse("an emptied section is removed with it");
    }

    [Fact]
    public async Task File_system_provider_restructures_the_data_directory()
    {
        using TestHarness harness = new();
        harness.WriteFile("images/logo.png", "binary");
        harness.WriteFile("data.sqlite", "db");
        harness.WriteFile("thumbnail.cache", "junk");
        harness.StampVersion(new Version(1, 0, 0));

        FileSystemMigrationProvider provider = new("Reorganise", operations => operations
            .EnsureDirectory("assets")
            .MoveDirectory("images", "assets/images")
            .RenameFile("data.sqlite", "app.db")
            .DeleteFile("thumbnail.cache"));

        MigrationEngine engine = new(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Reorganise", provider)]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("assets/images/logo.png").Should().Be("binary");
        harness.ReadFile("app.db").Should().Be("db");
        harness.FileExists("thumbnail.cache").Should().BeFalse();
        harness.FileExists("data.sqlite").Should().BeFalse();
    }

    [Fact]
    public void File_system_provider_is_forward_only_once_it_deletes_anything()
    {
        FileSystemMigrationProvider reversible = new("a", operations => operations.RenameFile("x", "y"));
        FileSystemMigrationProvider destructive = new("b", operations => operations.RenameFile("x", "y").DeleteFile("z"));

        reversible.CanDown.Should().BeTrue();
        destructive.CanDown.Should().BeFalse();
    }

    [Fact]
    public async Task File_system_provider_undoes_its_operations_in_reverse_order()
    {
        using TestHarness harness = new();
        harness.WriteFile("old/name.txt", "content");
        harness.StampVersion(new Version(1, 0, 0));

        FileSystemMigrationProvider provider = new("Restructure", operations => operations
            .EnsureDirectory("new")
            .MoveFile("old/name.txt", "new/name.txt"));

        MigrationOptions options = harness.CreateOptions();
        MigrationEngine up = new(options, [new MigrationStep(new Version(2, 0, 0), "Restructure", provider)]);
        await up.RunAsync();

        harness.ReadFile("new/name.txt").Should().Be("content");

        MigrationEngine down = new(
            harness.CreateOptions(o =>
            {
                o.TargetDataVersion = new Version(1, 0, 0);
                o.AllowDowngrade = true;
            }),
            [new MigrationStep(new Version(2, 0, 0), "Restructure", provider)]);

        MigrationResult result = await down.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("old/name.txt").Should().Be("content");
        Directory.Exists(Path.Combine(harness.DataDirectory, "new")).Should().BeFalse();
    }

    [Fact]
    public async Task File_system_provider_refuses_to_escape_the_working_directory()
    {
        using TestHarness harness = new();

        FileSystemMigrationProvider provider = new("Escape", operations => operations.DeleteFile("../../secrets.txt"));
        MigrationContextStub context = new(harness.DataDirectory);

        Func<Task> act = () => provider.UpAsync(context, null, CancellationToken.None);

        await act.Should().ThrowAsync<MigrationException>().WithMessage("*resolves outside the working directory*");
    }

    private sealed class MigrationContextStub(string workingDirectory) : IMigrationContext
    {
        public Version CurrentDataVersion => new(1, 0, 0);

        public Version TargetDataVersion => new(2, 0, 0);

        public MigrationDirection Direction => MigrationDirection.Upgrade;

        public InstallationModel Model => InstallationModel.InPlaceSingleFolder;

        public string WorkingDirectory { get; } = workingDirectory;

        public string OriginalDirectory => WorkingDirectory;

        public string? BackupDirectory => null;

        public IMigrationLogger Logger => NullMigrationLogger.Instance;

        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

        public string GetWorkingPath(string relativePath) => Path.Combine(WorkingDirectory, relativePath);
    }
}
