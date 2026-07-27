// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text.Json.Nodes;
using AwesomeAssertions;
using Barbatos.Migration.Json;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// JSON documents as they actually turn up in a shipped application: nested several levels deep,
/// arrays of objects and of primitives, mixed value types, nulls, Vietnamese text and format
/// placeholders. Modelled on the localisation files in Barbatos.i18n, whose v2 format nests its
/// entries under a section key.
/// </summary>
public class ComplexJsonTests
{
    private const string Complex = """
        {
          "version": "2.0",
          "editor": {
            "font": {
              "family": "Cascadia Code",
              "size": 14,
              "ligatures": true
            },
            "rulers": [ 80, 120 ],
            "wordWrap": null
          },
          "recentFiles": [
            { "path": "C:\\du-an\\a.txt", "pinned": false },
            { "path": "C:\\du-an\\b.txt", "pinned": true }
          ],
          "messages": {
            "greeting": "Xin chào {0}, mừng bạn quay lại!",
            "price": "Giá bán: {0:C}"
          },
          "pluginState": { "ribbon": "expanded", "unknownToUs": { "deep": [ 1, 2, 3 ] } }
        }
        """;

    private static JsonObject Parse(string text) => (JsonObject)JsonNode.Parse(text)!;

    private static async Task<JsonObject> MigrateAsync(TestHarness harness, Action<JsonObject> up)
    {
        harness.WriteFile("settings.json", Complex);
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Reshape", new JsonMigrationProvider("settings.json", up))]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        return Parse(harness.ReadFile("settings.json"));
    }

    [Fact]
    public async Task A_nested_value_is_reached_by_chaining_sections()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document =>
            document.Section("editor").Section("font").Set("size", 16));

        json["editor"]!["font"]!["size"]!.GetValue<int>().Should().Be(16);
        json["editor"]!["font"]!["family"]!.GetValue<string>().Should().Be("Cascadia Code");
    }

    [Fact]
    public async Task A_key_can_be_renamed_inside_a_nested_object()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document =>
            document.Section("editor").Section("font").RenameProperty("ligatures", "useLigatures"));

        json["editor"]!["font"]!["useLigatures"]!.GetValue<bool>().Should().BeTrue();
        (json["editor"]!["font"] as JsonObject)!.ContainsKey("ligatures").Should().BeFalse();
    }

    [Fact]
    public async Task A_key_moves_from_one_nested_object_into_another()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document =>
        {
            JsonObject editor = document.Section("editor");

            // editor.font.size -> editor.size -> editor.typography.fontSize
            editor.MoveOutOfSection("font", "size");
            editor.MoveIntoSection("size", "typography", "fontSize");
        });

        json["editor"]!["typography"]!["fontSize"]!.GetValue<int>().Should().Be(14);
        (json["editor"]!["font"] as JsonObject)!.ContainsKey("size").Should().BeFalse();
        json["editor"]!["font"]!["family"]!.GetValue<string>().Should().Be("Cascadia Code",
            "the section still has contents, so it is not removed");
    }

    [Fact]
    public async Task An_array_of_primitives_survives_untouched()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.SetDefault("language", "vi"));

        json["editor"]!["rulers"]!.AsArray().Select(node => node!.GetValue<int>()).Should().Equal([80, 120]);
    }

    [Fact]
    public async Task An_array_of_primitives_can_be_rewritten_through_ConvertProperty()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document =>
            document.Section("editor").ConvertProperty("rulers", value =>
                new JsonArray([.. value!.AsArray().Select(node => JsonValue.Create(node!.GetValue<int>() * 2))])));

        json["editor"]!["rulers"]!.AsArray().Select(node => node!.GetValue<int>()).Should().Equal([160, 240]);
    }

    [Fact]
    public async Task Every_object_in_an_array_can_be_given_a_new_field()
    {
        using TestHarness harness = new();

        // The shape a "each saved entry gains a property" migration takes.
        JsonObject json = await MigrateAsync(harness, document =>
            document.ForEachInArray("recentFiles", entry => entry
                .RenameProperty("path", "fullPath")
                .SetDefault("openedAt", "2026-07-27T00:00:00Z")));

        JsonArray recent = json["recentFiles"]!.AsArray();
        recent.Should().HaveCount(2);
        recent[0]!["fullPath"]!.GetValue<string>().Should().Be(@"C:\du-an\a.txt");
        recent[0]!["openedAt"]!.GetValue<string>().Should().Be("2026-07-27T00:00:00Z");
        recent[1]!["pinned"]!.GetValue<bool>().Should().BeTrue("values the migration did not mention are left alone");
    }

    [Fact]
    public void ForEachInArray_is_a_no_op_when_the_array_is_absent_or_not_an_array()
    {
        JsonObject json = Parse(Complex);

        Action act = () => json
            .ForEachInArray("notThere", _ => throw new InvalidOperationException("must not run"))
            .ForEachInArray("version", _ => throw new InvalidOperationException("must not run"))
            .ForEachInArray("editor", _ => throw new InvalidOperationException("must not run"));

        act.Should().NotThrow("a step re-run after a restored snapshot must not fail on a shape that has already changed");
    }

    [Fact]
    public void ForEachInArray_skips_entries_that_are_not_objects()
    {
        JsonObject json = Parse("""{ "mixed": [ { "a": 1 }, 42, null, { "a": 2 } ] }""");

        int visited = 0;
        json.ForEachInArray("mixed", _ => visited++);

        visited.Should().Be(2);
    }

    [Fact]
    public async Task A_deeply_nested_branch_nobody_understands_comes_back_byte_identical()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.RenameProperty("version", "schemaVersion"));

        // Written by a plugin, or by a newer build the user downgraded from. Nothing in the
        // migration mentions it, so all three levels of it survive.
        json["pluginState"]!["unknownToUs"]!["deep"]!.AsArray()
            .Select(node => node!.GetValue<int>()).Should().Equal([1, 2, 3]);
        json["pluginState"]!["ribbon"]!.GetValue<string>().Should().Be("expanded");
    }

    [Fact]
    public async Task A_null_value_is_preserved_rather_than_dropped()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.SetDefault("language", "vi"));

        (json["editor"] as JsonObject)!.ContainsKey("wordWrap").Should().BeTrue();
        json["editor"]!["wordWrap"].Should().BeNull("an explicit null is a value, not an absence");
    }

    [Fact]
    public async Task SetDefault_does_not_overwrite_an_explicit_null()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.Section("editor").SetDefault("wordWrap", true));

        json["editor"]!["wordWrap"].Should().BeNull("the key exists, so the user has already chosen");
    }

    [Fact]
    public async Task Vietnamese_text_and_format_placeholders_are_not_mangled()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.Section("messages").RenameProperty("price", "priceFormat"));

        json["messages"]!["greeting"]!.GetValue<string>().Should().Be("Xin chào {0}, mừng bạn quay lại!");
        json["messages"]!["priceFormat"]!.GetValue<string>().Should().Be("Giá bán: {0:C}");
    }

    [Fact]
    public async Task Vietnamese_text_stays_readable_on_disk_rather_than_being_escaped()
    {
        using TestHarness harness = new();

        await MigrateAsync(harness, document => document.SetDefault("language", "vi"));

        string onDisk = harness.ReadFile("settings.json");

        // System.Text.Json escapes every non-ASCII character by default, which would turn the
        // user's readable settings file into à soup because a migration renamed one key.
        onDisk.Should().Contain("Xin chào {0}, mừng bạn quay lại!");
        onDisk.Should().NotContain("\\u00E0");
    }

    [Fact]
    public async Task A_windows_path_keeps_its_backslashes()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document.SetDefault("language", "vi"));

        json["recentFiles"]![0]!["path"]!.GetValue<string>().Should().Be(@"C:\du-an\a.txt");
    }

    [Fact]
    public async Task A_whole_reshape_rolls_back_intact_when_a_later_step_fails()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.json", Complex);
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(options => options.TargetDataVersion = new Version(3, 0, 0)),
            [
                new MigrationStep(new Version(2, 0, 0), "Reshape",
                    new JsonMigrationProvider("settings.json", document => document
                        .RenameProperty("version", "schemaVersion")
                        .ForEachInArray("recentFiles", entry => entry.RenameProperty("path", "fullPath"))
                        .Section("editor").Section("font").Set("size", 20))),
                new MigrationStep(new Version(3, 0, 0), "Explodes",
                    new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("nope"))),
            ]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        harness.ReadFile("settings.json").Should().Be(Complex);
    }

    [Fact]
    public async Task Root_climbs_back_up_so_one_chain_can_edit_several_depths()
    {
        using TestHarness harness = new();

        JsonObject json = await MigrateAsync(harness, document => document
            .Section("editor").Section("font").Set("size", 18).Root()
            .Section("messages").Set("farewell", "Tạm biệt!").Root()
            .SetDefault("language", "vi"));

        json["editor"]!["font"]!["size"]!.GetValue<int>().Should().Be(18);
        json["messages"]!["farewell"]!.GetValue<string>().Should().Be("Tạm biệt!");
        json["language"]!.GetValue<string>().Should().Be("vi");
    }

    [Fact]
    public void Root_on_the_root_returns_the_document_itself()
    {
        JsonObject json = Parse(Complex);

        json.Root().Should().BeSameAs(json);
    }

    [Fact]
    public void Root_climbs_out_of_an_array_entry_too()
    {
        JsonObject json = Parse(Complex);

        JsonObject entry = (JsonObject)json["recentFiles"]![0]!;

        entry.Root().Should().BeSameAs(json);
    }

    [Fact]
    public void MoveIntoSection_merges_into_an_existing_section_rather_than_replacing_it()
    {
        JsonObject json = Parse("""{ "fontSize": 14, "editor": { "wordWrap": true } }""");

        json.MoveIntoSection("fontSize", "editor");

        json["editor"]!["fontSize"]!.GetValue<int>().Should().Be(14);
        json["editor"]!["wordWrap"]!.GetValue<bool>().Should().BeTrue("the section already had contents");
    }

    [Fact]
    public void Moving_a_nested_object_into_a_section_keeps_its_whole_subtree()
    {
        JsonObject json = Parse(Complex);

        json.MoveIntoSection("editor", "workspace");

        json["workspace"]!["editor"]!["font"]!["family"]!.GetValue<string>().Should().Be("Cascadia Code");
        json["workspace"]!["editor"]!["rulers"]!.AsArray().Should().HaveCount(2);
    }
}
