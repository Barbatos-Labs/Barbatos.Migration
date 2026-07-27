// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using AwesomeAssertions;
using Barbatos.Migration.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// Exercises <see cref="DatabaseMigrationProvider"/> against a real SQLite file, because the
/// interesting failures here are about the <em>file</em> - locks, handles, the write-ahead log -
/// and an in-memory fake would prove nothing about any of them.
/// </summary>
public class DatabaseProviderTests
{
    private const string CreateSchema = """
        CREATE TABLE Users (Id INTEGER PRIMARY KEY, FullName TEXT NOT NULL);
        INSERT INTO Users (Id, FullName) VALUES (1, 'Pham The Hung'), (2, 'Ada Lovelace');
        """;

    private static DbConnection OpenConnection(string path) =>
        new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

    private static DatabaseMigrationProvider Provider(IEnumerable<string> up, IEnumerable<string>? down = null) =>
        DatabaseMigrationProvider.ForFile("app.db", OpenConnection, up, down, DatabaseDialects.Sqlite);

    private static void Seed(TestHarness harness)
    {
        using SqliteConnection connection = (SqliteConnection)OpenConnection(Path.Combine(harness.DataDirectory, "app.db"));
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = CreateSchema;
        command.ExecuteNonQuery();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test assertions with constant SQL from this file.")]
    private static List<string> ReadColumn(TestHarness harness, string sql)
    {
        using SqliteConnection connection = (SqliteConnection)OpenConnection(Path.Combine(harness.DataDirectory, "app.db"));
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        List<string> values = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

        return values;
    }

    [Fact]
    public async Task Scripts_run_in_order_and_commit()
    {
        using TestHarness harness = new();
        Seed(harness);
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Split the name column", Provider([
                "ALTER TABLE Users ADD COLUMN FirstName TEXT;",
                "UPDATE Users SET FirstName = substr(FullName, 1, instr(FullName, ' ') - 1);",
            ]))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        ReadColumn(harness, "SELECT FirstName FROM Users ORDER BY Id;").Should().Equal("Pham", "Ada");
    }

    [Fact]
    public async Task A_failing_statement_rolls_the_transaction_back_and_restores_the_data_directory()
    {
        using TestHarness harness = new();
        Seed(harness);
        harness.WriteFile("settings.json", """{ "theme": "dark" }""");
        harness.StampVersion(new Version(1, 0, 0));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Broken migration", Provider([
                "ALTER TABLE Users ADD COLUMN FirstName TEXT;",
                "UPDATE Users SET FirstName = 'x';",
                "THIS IS NOT SQL;",
            ]))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        result.Error!.Message.Should().Contain("Statement 3 of 3 failed");

        // The transaction covers the first two statements, and the engine's snapshot covers the
        // whole directory. Either alone would leave something behind; together the database is
        // exactly what it was.
        ReadColumn(harness, "SELECT FullName FROM Users ORDER BY Id;").Should().Equal("Pham The Hung", "Ada Lovelace");
        ReadColumn(harness, "SELECT name FROM pragma_table_info('Users');").Should().Equal("Id", "FullName");
        harness.ReadFile("settings.json").Should().Be("""{ "theme": "dark" }""");
    }

    [Fact]
    public async Task The_database_file_is_released_so_the_engine_can_swap_the_directory()
    {
        using TestHarness harness = new();
        Seed(harness);
        harness.StampVersion(new Version(1, 0, 0));

        // The provider succeeds; a later provider in the same step throws. Rolling back means
        // renaming the directory the database file sits in, which Windows refuses while any
        // handle to it is open. This is the scenario that silently breaks a migration framework
        // that forgets to clear the connection pool.
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(
                new Version(2, 0, 0),
                "Database then failure",
                Provider(["ALTER TABLE Users ADD COLUMN Email TEXT;"]),
                new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("after the database")))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed, "the rollback must not be blocked by an open database handle");
        result.RollbackError.Should().BeNull();
        ReadColumn(harness, "SELECT name FROM pragma_table_info('Users');").Should().Equal("Id", "FullName");
    }

    [Fact]
    public async Task Down_scripts_reverse_the_change()
    {
        using TestHarness harness = new();
        Seed(harness);
        harness.StampVersion(new Version(1, 0, 0));

        DatabaseMigrationProvider provider = Provider(
            up: ["ALTER TABLE Users ADD COLUMN Email TEXT;"],
            down: ["ALTER TABLE Users DROP COLUMN Email;"]);

        provider.CanDown.Should().BeTrue();

        MigrationStep step = new(new Version(2, 0, 0), "Add email", provider);

        await new MigrationEngine(harness.CreateOptions(), [step]).RunAsync();
        ReadColumn(harness, "SELECT name FROM pragma_table_info('Users');").Should().Contain("Email");

        MigrationResult down = await new MigrationEngine(
            harness.CreateOptions(options =>
            {
                options.TargetDataVersion = new Version(1, 0, 0);
                options.AllowDowngrade = true;
            }),
            [step]).RunAsync();

        down.Outcome.Should().Be(MigrationOutcome.Succeeded);
        ReadColumn(harness, "SELECT name FROM pragma_table_info('Users');").Should().NotContain("Email");
    }

    [Fact]
    public async Task A_migration_that_orphans_rows_is_rejected_before_it_commits()
    {
        using TestHarness harness = new();

        using (SqliteConnection connection = (SqliteConnection)OpenConnection(Path.Combine(harness.DataDirectory, "app.db")))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY, Name TEXT);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorId INTEGER REFERENCES Authors(Id));
                INSERT INTO Authors (Id, Name) VALUES (1, 'Ada');
                INSERT INTO Books (Id, AuthorId) VALUES (1, 1);
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        harness.StampVersion(new Version(1, 0, 0));

        // Deleting the author leaves the book pointing at nothing. Foreign keys are suspended
        // while the scripts run, so nothing complains until the dialect's own check - which runs
        // inside the transaction, and therefore turns this into a rollback rather than a
        // committed, quietly broken database.
        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Drop the author", Provider(["DELETE FROM Authors WHERE Id = 1;"]))]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        result.Error!.Message.Should().Contain("foreign key violations");
        ReadColumn(harness, "SELECT Name FROM Authors;").Should().Equal("Ada");
    }

    [Fact]
    public void Dialects_declare_whether_schema_changes_are_transactional()
    {
        DatabaseDialects.Sqlite.SupportsTransactionalSchemaChanges.Should().BeTrue();
        DatabaseDialects.PostgreSql.SupportsTransactionalSchemaChanges.Should().BeTrue();
        DatabaseDialects.SqlServer.SupportsTransactionalSchemaChanges.Should().BeTrue();

        // MySQL commits DDL implicitly. Getting this wrong is how a framework promises
        // atomicity it cannot deliver, so the provider warns rather than staying quiet.
        DatabaseDialects.MySql.SupportsTransactionalSchemaChanges.Should().BeFalse();
    }

    [Fact]
    public async Task A_non_transactional_dialect_warns_that_the_step_cannot_be_rolled_back_as_a_unit()
    {
        using TestHarness harness = new();
        Seed(harness);
        harness.StampVersion(new Version(1, 0, 0));

        DatabaseMigrationProvider provider = DatabaseMigrationProvider.ForFile(
            "app.db",
            OpenConnection,
            ["ALTER TABLE Users ADD COLUMN Email TEXT;"],
            down: null,
            dialect: DatabaseDialects.MySql);

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Add email", provider)]);

        await engine.RunAsync();

        harness.LogMessages.Should().Contain(message => message.Contains("commits schema changes implicitly"));
    }
}
