// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Csv;
using Barbatos.Migration.Database;
using Barbatos.Migration.Json;

namespace Barbatos.Migration.Wpf.Sample.Migrations;

/// <summary>
/// One conceptual change - names are now stored as two fields - applied everywhere it shows up:
/// the database, the exported table beside it, and the setting that controls how they are
/// displayed. All three belong to the same step, so they are applied, or undone, together.
/// </summary>
[MigrationStep("2.0.0", "Split the full name into first and last")]
public sealed class SplitFullName : MigrationStepBase
{
    protected override IEnumerable<IMigrationProvider> CreateProviders()
    {
        DatabaseMigrationProvider database = DatabaseMigrationProvider.ForFile(
            SampleData.DatabaseFileName,
            SampleData.OpenConnection,
            up:
            [
                "ALTER TABLE Users ADD COLUMN FirstName TEXT;",
                "ALTER TABLE Users ADD COLUMN LastName TEXT;",
                """
                UPDATE Users
                SET FirstName = substr(FullName, 1, instr(FullName || ' ', ' ') - 1),
                    LastName  = trim(substr(FullName, instr(FullName || ' ', ' ')));
                """,

                // Deliberately invalid, only when the playground's "Upgrade, then fail" button
                // asks for it. Everything above has already been applied inside the transaction
                // by this point, which is what makes the rollback worth watching.
                SampleData.SimulateFailure ? "THIS STATEMENT IS NOT VALID SQL;" : "SELECT 1;",
            ],
            down:
            [
                "UPDATE Users SET FullName = trim(FirstName || ' ' || LastName);",
                "ALTER TABLE Users DROP COLUMN FirstName;",
                "ALTER TABLE Users DROP COLUMN LastName;",
            ],
            dialect: DatabaseDialects.Sqlite);

        database.Options.SchemaVersion = 2;
        yield return database;

        // The same split, on the CSV export. SplitColumn puts the new columns exactly where the
        // old one was, and MergeColumns is its exact inverse - which is what makes this step
        // reversible without hand-writing the undo.
        yield return new CsvMigrationProvider(
            SampleData.LicencesFileName,
            up: csv => csv
                .SplitColumn("FullName", name => name.Split(' ', 2), "FirstName", "LastName")
                .AddColumn("Archived", "false"),
            down: csv => csv
                .RemoveColumn("Archived")
                .MergeColumns("FullName", parts => string.Join(" ", parts.Where(part => part.Length > 0)), "FirstName", "LastName"));

        yield return new JsonMigrationProvider(
            SampleData.SettingsFileName,
            up: json => json.Section("editor").Set("showFullName", false),
            down: json => json.Section("editor").RemoveProperty("showFullName"));
    }
}
