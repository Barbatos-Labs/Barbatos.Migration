// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Ini;
using Barbatos.Migration.Json;

namespace Barbatos.Migration.Wpf.Sample.Migrations;

/// <summary>
/// Settings grew enough to need grouping - in both of the places this app keeps them. Two
/// providers, one step, applied and undone together.
/// </summary>
/// <remarks>
/// Both files here are deliberately awkward: the JSON nests three levels deep, carries an array
/// of objects and a branch written by a third-party plugin; the INI has a colon in a section
/// name, a quoted value containing a semicolon, a connection string whose semicolons are not
/// comments, and comments the user maintains by hand. Open them before and after an upgrade -
/// everything the migration did not mention is exactly where it was.
/// </remarks>
[MigrationStep("1.1.0", "Group the editor settings")]
public sealed class GroupEditorSettings : MigrationStepBase
{
    protected override IEnumerable<IMigrationProvider> CreateProviders()
    {
        yield return new JsonMigrationProvider(
            SampleData.SettingsFileName,
            up: json => json
                // Top-level keys move down into the section that already exists, merging with
                // what is in it rather than replacing it.
                .MoveIntoSection("fontSize", "editor")
                .MoveIntoSection("wordWrap", "editor")

                // Reached by chaining, because the key lives three levels down.
                .Section("editor").Section("minimap").Set("side", "left").Root()

                // Every entry in an array of objects gains a field and loses a rename.
                .ForEachInArray("recentFiles", entry => entry
                    .RenameProperty("path", "fullPath")
                    .SetDefault("openedAt", "2026-01-01T00:00:00Z"))

                .SetDefault("language", "vi"),

            down: json => json
                .RemoveProperty("language")
                .ForEachInArray("recentFiles", entry => entry
                    .RemoveProperty("openedAt")
                    .RenameProperty("fullPath", "path"))
                .Section("editor").Section("minimap").Set("side", "right").Root()
                .MoveOutOfSection("editor", "wordWrap")
                .MoveOutOfSection("editor", "fontSize"));

        yield return new IniMigrationProvider(
            SampleData.LegacySettingsFileName,
            up: ini => ini
                .RenameSection("Plugins", "Extensions")
                .RenameKey("Extensions", "ribbonState", "ribbon")
                .RenameSection("Plugins:Telemetry", "Extensions:Telemetry")
                .ConvertValue("Extensions:Telemetry", "Timeout:Max", seconds => $"{int.Parse(seconds) * 1000}")
                .SetDefault("Extensions", "autoUpdate", "true"),

            down: ini => ini
                .RemoveKey("Extensions", "autoUpdate")
                .ConvertValue("Extensions:Telemetry", "Timeout:Max", milliseconds => $"{int.Parse(milliseconds) / 1000}")
                .RenameSection("Extensions:Telemetry", "Plugins:Telemetry")
                .RenameKey("Extensions", "ribbon", "ribbonState")
                .RenameSection("Extensions", "Plugins"));
    }
}
