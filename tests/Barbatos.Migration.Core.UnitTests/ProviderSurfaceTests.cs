// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Barbatos.Migration.Csv;
using Barbatos.Migration.Database;
using Barbatos.Migration.FileSystem;
using Barbatos.Migration.Ini;
using Barbatos.Migration.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// The members of each provider package that the scenario tests do not happen to exercise -
/// so that every published method has at least one worked example.
/// </summary>
public class ProviderSurfaceTests
{
    // ------------------------------------------------------------------ core provider bases

    [Fact]
    public async Task MigrationProvider_is_forward_only_by_default()
    {
        SimpleProvider provider = new();

        provider.Weight.Should().Be(1.0);
        provider.CanDown.Should().BeFalse();

        Func<Task> act = () => provider.DownAsync(null!, null, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*forward-only*");
    }

    [Fact]
    public async Task DelegateMigrationProvider_runs_its_delegates()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        DelegateMigrationProvider provider = new(
            "inline",
            up: (context, progress, _) =>
            {
                progress?.Report(new MigrationProgress(50, "working"));
                File.WriteAllText(context.GetWorkingPath("up.txt"), "up");
                return Task.CompletedTask;
            },
            down: (context, _, _) =>
            {
                File.Delete(context.GetWorkingPath("up.txt"));
                return Task.CompletedTask;
            },
            weight: 3.5);

        provider.Name.Should().Be("inline");
        provider.Weight.Should().Be(3.5);
        provider.CanDown.Should().BeTrue();

        await new MigrationEngine(harness.CreateOptions(), [new MigrationStep(new Version(2, 0, 0), "Inline", provider)]).RunAsync();
        harness.FileExists("up.txt").Should().BeTrue();
    }

    [Fact]
    public void DelegateMigrationProvider_validates_its_arguments()
    {
        ((Action)(() => new DelegateMigrationProvider("", (_, _, _) => Task.CompletedTask)))
            .Should().Throw<ArgumentException>();

        ((Action)(() => new DelegateMigrationProvider("n", null!)))
            .Should().Throw<ArgumentNullException>();

        ((Action)(() => new DelegateMigrationProvider("n", (_, _, _) => Task.CompletedTask, weight: 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NullMigrationLogger_swallows_everything_and_DelegateMigrationLogger_forwards()
    {
        NullMigrationLogger.Instance.Log(MigrationLogLevel.Critical, "ignored");

        List<string> written = [];
        new DelegateMigrationLogger((level, message, _) => written.Add($"{level}:{message}"))
            .Log(MigrationLogLevel.Warning, "heard");

        written.Should().Equal(["Warning:heard"]);

        ((Action)(() => new DelegateMigrationLogger(null!))).Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------ json

    [Fact]
    public void Json_helpers_cover_set_remove_convert_and_section()
    {
        JsonObject json = JsonNode.Parse("""{ "timeoutSeconds": 90, "old": 1 }""")!.AsObject();

        json.Set("theme", "dark")
            .RemoveProperty("old")
            .ConvertProperty("timeoutSeconds", value => TimeSpan.FromSeconds(value!.GetValue<int>()).ToString("c"));

        json.Section("editor").Set("wordWrap", true);

        json["theme"]!.GetValue<string>().Should().Be("dark");
        json.ContainsKey("old").Should().BeFalse();
        json["timeoutSeconds"]!.GetValue<string>().Should().Be("00:01:30");
        json["editor"]!["wordWrap"]!.GetValue<bool>().Should().BeTrue();

        // Section returns the existing object rather than replacing it.
        json.Section("editor").Should().BeSameAs((JsonObject)json["editor"]!);
    }

    [Fact]
    public void Json_helpers_reject_a_null_document()
    {
        JsonObject? nothing = null;

        ((Action)(() => nothing!.RenameProperty("a", "b"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => nothing!.ConvertProperty("a", value => value))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new JsonObject().ConvertProperty("a", null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Json_createIfMissing_false_leaves_an_absent_file_alone()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Optional file",
                new JsonMigrationProvider("optional.json", json => json.Set("x", 1), createIfMissing: false))]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.FileExists("optional.json").Should().BeFalse();
    }

    // ------------------------------------------------------------------ ini

    [Fact]
    public void Ini_reading_helpers_describe_the_document()
    {
        IniDocument document = IniDocument.Parse("version = 3\n\n[General]\nlanguage = vi\n\n[Editor]\nfontSize = 14\n");

        document.SectionNames.Should().Equal(["General", "Editor"]);
        document.ContainsSection("general").Should().BeTrue("names are case-insensitive by default");
        document.ContainsSection("Nope").Should().BeFalse();
        document.KeysIn("General").Should().Equal(["language"]);
        document.GetValue("General", "missing").Should().BeNull();
        document.GetValue("General", "missing", "fallback").Should().Be("fallback");
        document.GetValue(string.Empty, "version").Should().Be("3");
    }

    [Fact]
    public void Ini_case_sensitivity_can_be_turned_on()
    {
        IniDocument sensitive = IniDocument.Parse("[General]\nLanguage = vi\n", caseSensitive: true);

        sensitive.GetValue("General", "language").Should().BeNull();
        sensitive.GetValue("General", "Language").Should().Be("vi");
    }

    [Fact]
    public void Ini_EnsureSection_and_AddComment_extend_the_document()
    {
        IniDocument document = IniDocument.Parse("[General]\nlanguage = vi\n");

        document.EnsureSection("Editor")
            .AddComment("Editor", "Added by the 2.0.0 migration")
            .Set("Editor", "fontSize", "14");

        string result = document.ToIniString();

        result.Should().Contain("[Editor]")
            .And.Contain("; Added by the 2.0.0 migration")
            .And.Contain("fontSize = 14");

        // Ensuring an existing section is a no-op, so a re-run does not duplicate it.
        document.EnsureSection("Editor");
        document.ToIniString().Split("[Editor]").Should().HaveCount(2);
    }

    [Fact]
    public void Ini_RemoveSection_takes_the_whole_block_including_its_blank_line()
    {
        IniDocument document = IniDocument.Parse("[A]\nk = 1\n\n[B]\nj = 2\n");

        document.RemoveSection("A");

        document.ToIniString().Should().Be("[B]\nj = 2\n");
        document.ContainsSection("A").Should().BeFalse();
    }

    [Fact]
    public void Ini_new_lines_use_the_documents_own_conventions()
    {
        IniDocument document = IniDocument.Parse("[A]\r\nk = 1\r\n");
        document.KeyValueSeparator = "=";
        document.CommentPrefix = '#';

        document.Set("A", "added", "yes").AddComment("A", "note");

        document.ToIniString().Should().Contain("added=yes").And.Contain("# note");
    }

    // ------------------------------------------------------------------ csv

    [Fact]
    public void Csv_Create_builds_a_document_from_nothing()
    {
        CsvDocument document = CsvDocument.Create(["Id", "Name"], delimiter: ';');

        document.AddRow("1", "Ada");
        document.AddRow([new KeyValuePair<string, string>("Id", "2"), new KeyValuePair<string, string>("Name", "Grace")]);
        document.EndsWithNewLine = false;

        document.Delimiter.Should().Be(';');
        document.ToCsvString().Should().Be($"Id;Name{document.NewLine}1;Ada{document.NewLine}2;Grace");
    }

    [Fact]
    public void Csv_MoveColumn_takes_the_values_with_it()
    {
        CsvDocument document = CsvDocument.Parse("Id,Name,Email\n1,Ada,ada@example.com\n");

        document.MoveColumn("Email", 0);

        document.Columns.Should().Equal(["Email", "Id", "Name"]);
        document.Rows[0].Values.Should().Equal(["ada@example.com", "1", "Ada"]);
    }

    [Theory]
    [InlineData(CsvQuoteStyle.Minimal, "Id,Name\n1,Ada\n")]
    [InlineData(CsvQuoteStyle.All, "\"Id\",\"Name\"\n\"1\",\"Ada\"\n")]
    public void Csv_quote_style_controls_the_output(CsvQuoteStyle style, string expected)
    {
        CsvDocument document = CsvDocument.Parse("Id,Name\n1,Ada\n");
        document.QuoteStyle = style;

        document.ToCsvString().Should().Be(expected);
    }

    [Fact]
    public void Csv_PreserveOriginal_keeps_quotes_the_author_chose()
    {
        CsvDocument document = CsvDocument.Parse("Id,Name\n1,\"Ada\"\n");

        document.QuoteStyle.Should().Be(CsvQuoteStyle.PreserveOriginal);
        document.ToCsvString().Should().Be("Id,Name\n1,\"Ada\"\n");
    }

    [Fact]
    public void Csv_row_helpers_answer_the_obvious_questions()
    {
        CsvDocument document = CsvDocument.Parse("Id,Name\n1,Ada\n,\n");

        document.Rows[0].FieldCount.Should().Be(2);
        document.Rows[0].IsEmpty.Should().BeFalse();
        document.Rows[1].IsEmpty.Should().BeTrue();
        document.Rows[0].ToString().Should().Be("1, Ada");

        document.IndexOf("Name").Should().Be(1);
        document.IndexOf("Nope").Should().Be(-1);
        document.ContainsColumn("name").Should().BeTrue();
    }

    [Fact]
    public void Csv_writing_to_a_column_that_does_not_exist_says_so()
    {
        CsvDocument document = CsvDocument.Parse("Id\n1\n");

        Action act = () => document.Rows[0]["Missing"] = "x";

        act.Should().Throw<MigrationException>().WithMessage("*no column called 'Missing'*");
    }

    // ------------------------------------------------------------------ file system

    [Fact]
    public async Task FileSystem_copy_write_and_delete_directory_operations()
    {
        using TestHarness harness = new();
        harness.WriteFile("source.txt", "content");
        harness.WriteFile("cache/old.tmp", "junk");
        harness.StampVersion(new Version(1, 0, 0));

        FileSystemMigrationProvider provider = new("Restructure", operations => operations
            .CopyFile("source.txt", "backup/source.txt")
            .WriteText("readme.txt", "Written by the 2.0.0 migration")
            .DeleteDirectory("cache"));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Restructure", provider)]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("backup/source.txt").Should().Be("content");
        harness.ReadFile("source.txt").Should().Be("content", "a copy leaves the original in place");
        harness.ReadFile("readme.txt").Should().Be("Written by the 2.0.0 migration");
        Directory.Exists(Path.Combine(harness.DataDirectory, "cache")).Should().BeFalse();
    }

    [Fact]
    public void FileSystem_rejects_an_absolute_path_and_an_empty_operation_list()
    {
        FileSystemMigrationProvider provider = new("Escape", operations => operations.MoveFile(@"C:\Windows\x", "y"));
        MigrationContextStub context = new(Path.GetTempPath());

        Func<Task> act = () => provider.UpAsync(context, null, CancellationToken.None);
        act.Should().ThrowAsync<MigrationException>().WithMessage("*absolute path*");

        ((Action)(() => new FileSystemMigrationProvider("Empty", _ => { })))
            .Should().Throw<ArgumentException>().WithMessage("*no file system operations*");
    }

    // ------------------------------------------------------------------ database

    [Fact]
    public async Task Database_options_control_timeouts_isolation_and_the_schema_version()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "app.db");

        using (SqliteConnection seed = new($"Data Source={path};Pooling=False"))
        {
            seed.Open();
            using SqliteCommand command = seed.CreateCommand();
            command.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        harness.StampVersion(new Version(1, 0, 0));

        DatabaseMigrationProvider provider = DatabaseMigrationProvider.ForFile(
            "app.db",
            file => new SqliteConnection($"Data Source={file};Pooling=False"),
            ["ALTER TABLE T ADD COLUMN Name TEXT;"],
            dialect: DatabaseDialects.Sqlite);

        provider.Options.SchemaVersion = 7;
        provider.Options.CommandTimeoutSeconds = 30;
        provider.IsolationLevel = IsolationLevel.Serializable;
        provider.Weight = 9.0;

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Alter", provider)]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);

        using SqliteConnection check = new($"Data Source={path};Pooling=False");
        check.Open();
        using SqliteCommand version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(version.ExecuteScalar()).Should().Be(7);
    }

    [Fact]
    public void The_generic_dialect_does_nothing_engine_specific()
    {
        DatabaseDialects.Generic.Name.Should().Be("Generic");
        DatabaseDialects.Generic.SupportsTransactionalSchemaChanges.Should().BeTrue();

        // No connection is touched, so a dialect for an engine nobody has written one for yet
        // is still safe to use.
        Func<Task> act = async () =>
        {
            await DatabaseDialects.Generic.PrepareAsync(null!, new DatabaseMigrationOptions(), CancellationToken.None);
            await DatabaseDialects.Generic.FinishAsync(null!, new DatabaseMigrationOptions(), CancellationToken.None);
            DatabaseDialects.Generic.ReleaseResources(null!);
        };

        act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_custom_dialect_hooks_into_every_stage()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        TracingDialect dialect = new();

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Alter", DatabaseMigrationProvider.ForFile(
                "app.db",
                file => new SqliteConnection($"Data Source={file};Pooling=False"),
                ["CREATE TABLE T (Id INTEGER PRIMARY KEY);"],
                dialect: dialect))]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        dialect.Stages.Should().Equal(["prepare", "verify", "finish", "release"]);
    }

    [Fact]
    public void The_provider_validates_its_arguments()
    {
        ((Action)(() => new DatabaseMigrationProvider("", _ => null!, ["SELECT 1;"])))
            .Should().Throw<ArgumentException>();

        ((Action)(() => new DatabaseMigrationProvider("n", null!, ["SELECT 1;"])))
            .Should().Throw<ArgumentNullException>();

        ((Action)(() => new DatabaseMigrationProvider("n", _ => null!, [])))
            .Should().Throw<ArgumentException>().WithMessage("*At least one statement*");

        ((Action)(() => DatabaseMigrationProvider.ForFile("", _ => null!, ["SELECT 1;"])))
            .Should().Throw<ArgumentException>();
    }

    private sealed class SimpleProvider : MigrationProvider
    {
        public override string Name => "simple";

        public override Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TracingDialect : DatabaseDialect
    {
        public List<string> Stages { get; } = [];

        public override string Name => "Tracing";

        public override Task PrepareAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken)
        {
            Stages.Add("prepare");
            return Task.CompletedTask;
        }

        public override Task VerifyAsync(DbConnection connection, DbTransaction transaction, DatabaseMigrationOptions options, CancellationToken cancellationToken)
        {
            Stages.Add("verify");
            return Task.CompletedTask;
        }

        public override Task FinishAsync(DbConnection connection, DatabaseMigrationOptions options, CancellationToken cancellationToken)
        {
            Stages.Add("finish");
            return Task.CompletedTask;
        }

        public override void ReleaseResources(DbConnection connection) => Stages.Add("release");
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
