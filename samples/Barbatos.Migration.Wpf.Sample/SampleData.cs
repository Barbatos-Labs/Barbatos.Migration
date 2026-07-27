// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Barbatos.Migration.Wpf.Sample;

/// <summary>
/// The file names the sample's steps operate on, the connection factory they share, and the
/// seed data the playground starts from.
/// </summary>
/// <remarks>
/// The steps themselves live one-per-file under <c>Migrations/</c>, each declared with
/// <c>[Migration]</c> and found by <c>AddStepsFromAssembly()</c> - see
/// <see cref="WpfProgram.CreateWpfApp"/>. Nothing here lists them.
/// </remarks>
public static class SampleData
{
    /// <summary>The version a freshly seeded data folder starts at.</summary>
    public static readonly Version InitialVersion = new(1, 0, 0);

    /// <summary>The version this build of the application needs.</summary>
    public static readonly Version CurrentVersion = new(2, 0, 0);

    /// <summary>The database file inside the data directory.</summary>
    public const string DatabaseFileName = "app.db";

    /// <summary>The settings file inside the data directory.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>A legacy INI settings file, as an application that predates the framework would have.</summary>
    public const string LegacySettingsFileName = "plugins.ini";

    /// <summary>An exported data table, migrated alongside the database it came from.</summary>
    public const string LicencesFileName = "licences.csv";

    /// <summary>
    /// Makes the 2.0.0 step fail after it has already changed the database, so the playground's
    /// "Upgrade, then fail" button can show the engine putting everything back.
    /// </summary>
    /// <remarks>
    /// A static switch because a step discovered by scanning is constructed by the framework and
    /// cannot be handed a parameter. Real applications have no reason to want one.
    /// </remarks>
    public static bool SimulateFailure { get; set; }

    /// <summary>
    /// Opens a connection to the sample database.
    /// </summary>
    /// <remarks>
    /// <c>Pooling=False</c> matters more than it looks. A pooled handle outliving the migration
    /// keeps the database file open, and on Windows that makes the engine's snapshot, restore
    /// and directory rename all fail - so a rollback would be blocked by the very file it is
    /// trying to restore. <see cref="Database.DatabaseDialects.Sqlite"/> also clears the
    /// driver's pools afterwards, as a second line of defence against handles opened elsewhere.
    /// </remarks>
    public static DbConnection OpenConnection(string path) =>
        new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

    /// <summary>
    /// Writes a data folder in its original 1.0.0 shape, as an installation that has been in use
    /// for a while would look.
    /// </summary>
    public static void SeedVersion1(string directory)
    {
        Directory.CreateDirectory(directory);

        // Nested several levels deep, with an array of objects and a branch this application
        // knows nothing about - the shape a real settings file reaches after a few releases.
        File.WriteAllText(
            Path.Combine(directory, SettingsFileName),
            """
            {
              "schemaVersion": "1.0",
              "theme": "dark",
              "fontSize": 14,
              "wordWrap": true,
              "editor": {
                "rulers": [ 80, 120 ],
                "minimap": { "enabled": true, "side": "right" }
              },
              "recentFiles": [
                { "path": "C:\\du-an\\ghi-chu.txt", "pinned": false },
                { "path": "C:\\du-an\\ban-nhap.txt", "pinned": true }
              ],
              "messages": {
                "greeting": "Xin chào {0}, mừng bạn quay lại!",
                "price": "Giá bán: {0:C}"
              },
              "pluginRibbonState": "expanded",
              "thirdPartyPlugin": { "layout": { "panes": [ "left", "bottom" ] } }
            }
            """);

        // Written the way a version of the app from before the migration framework would have:
        // comments the user maintains by hand, a section name with a colon in it, a quoted value
        // containing a semicolon, and a connection string whose semicolons are *not* comments.
        File.WriteAllText(
            Path.Combine(directory, LegacySettingsFileName),
            "; Cau hinh plugin - nguoi dung tu sua tay\r\n" +
            "; Dung xoa cac dong ghi chu nay!\r\n" +
            "\r\n" +
            "[Plugins]\r\n" +
            "ribbonState = expanded    ; expanded | collapsed\r\n" +
            "recentLimit = 20\r\n" +
            "\r\n" +
            "[Plugins:Telemetry]\r\n" +
            "Endpoint=\"https://example.com/ingest; retry=3\"\r\n" +
            "Timeout:Max=30 ; tinh bang giay\r\n" +
            "\r\n" +
            "; Ghi chu cho phan Advanced - phai con lai sau khi migrate\r\n" +
            "[Advanced]\r\n" +
            "verboseLogging = false\r\n" +
            "ConnectionString=Server=localhost;Database=app;Timeout=15\r\n");

        // An exported table that has to move with the database schema beside it.
        File.WriteAllText(
            Path.Combine(directory, LicencesFileName),
            "Id,FullName,Email,IssuedOn\r\n" +
            "1,Pham The Hung,hung@example.com,2026-01-15\r\n" +
            "2,\"Lovelace, Ada\",ada@example.com,2026-02-02\r\n" +
            "3,Grace Hopper,grace@example.com,2026-03-11\r\n");

        using DbConnection connection = OpenConnection(Path.Combine(directory, DatabaseFileName));
        connection.Open();

        using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (Id INTEGER PRIMARY KEY, FullName TEXT NOT NULL);
            DELETE FROM Users;
            INSERT INTO Users (Id, FullName) VALUES
                (1, 'Pham The Hung'),
                (2, 'Ada Lovelace'),
                (3, 'Grace Hopper');
            """;
        command.ExecuteNonQuery();

        connection.Close();
        SqliteConnection.ClearAllPools();

        new FileDataVersionStore(directory).Write(InitialVersion, []);
    }
}
