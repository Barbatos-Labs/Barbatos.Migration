// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Migration.Ini;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class IniProviderTests
{
    private const string Sample =
        "; Cai dat cua toi - dung xoa!\r\n" +
        "\r\n" +
        "[General]\r\n" +
        "language = vi\r\n" +
        "fontSize = 14      ; co chu mac dinh\r\n" +
        "\r\n" +
        "[Plugins]\r\n" +
        "ribbonState = expanded\r\n";

    [Fact]
    public void An_untouched_document_round_trips_byte_for_byte()
    {
        IniDocument.Parse(Sample).ToIniString().Should().Be(Sample);
    }

    [Fact]
    public void Renaming_a_key_keeps_its_comment_its_spacing_and_everything_around_it()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.RenameKey("General", "fontSize", "fontSizePx");

        string result = document.ToIniString();

        // The whole point of the format-preserving model: one identifier changed, nothing else.
        result.Should().Be(Sample.Replace("fontSize = 14", "fontSizePx = 14"));
        result.Should().Contain("; Cai dat cua toi - dung xoa!");
        result.Should().Contain("fontSizePx = 14      ; co chu mac dinh");
    }

    [Fact]
    public void Changing_a_value_leaves_the_trailing_comment_where_it_was()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.Set("General", "fontSize", "16");

        document.ToIniString().Should().Contain("fontSize = 16      ; co chu mac dinh");
    }

    [Fact]
    public void A_new_key_lands_in_its_own_section_not_at_the_end_of_the_file()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.Set("General", "theme", "dark");

        string result = document.ToIniString();
        result.IndexOf("theme = dark", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("[Plugins]", StringComparison.Ordinal));
    }

    [Fact]
    public void SetDefault_leaves_a_value_the_user_already_chose()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.SetDefault("General", "language", "en");

        document.GetValue("General", "language").Should().Be("vi");
    }

    [Fact]
    public void Moving_a_key_between_sections_carries_its_value_across()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.MoveKey("General", "fontSize", "Editor", "fontSizePx");

        document.ContainsKey("General", "fontSize").Should().BeFalse();
        document.GetValue("Editor", "fontSizePx").Should().Be("14");
        document.ToIniString().Should().Contain("[Editor]");
    }

    [Fact]
    public void Renaming_a_section_keeps_the_keys_in_it()
    {
        IniDocument document = IniDocument.Parse(Sample);

        document.RenameSection("Plugins", "Extensions");

        document.ToIniString().Should().Contain("[Extensions]").And.NotContain("[Plugins]");
        document.GetValue("Extensions", "ribbonState").Should().Be("expanded");
    }

    [Fact]
    public void A_value_that_would_be_read_back_as_a_comment_is_quoted()
    {
        IniDocument document = IniDocument.Parse("[A]\nkey = old\n");

        // A space before the ';' is what makes a reader treat the rest as a comment, so this is
        // the value that has to be quoted. One without the space - a connection string, say -
        // round-trips bare and is left alone.
        document.Set("A", "key", "value ; with semicolon");

        string result = document.ToIniString();
        result.Should().Contain("\"value ; with semicolon\"");

        // And it survives a round trip, which is the only reason the quoting exists.
        IniDocument.Parse(result).GetValue("A", "key").Should().Be("value ; with semicolon");
    }

    [Fact]
    public void Line_endings_are_preserved_so_the_first_migration_is_not_a_whole_file_change()
    {
        IniDocument.Parse("[A]\nk = v\n").ToIniString().Should().Be("[A]\nk = v\n");
        IniDocument.Parse("[A]\r\nk = v\r\n").ToIniString().Should().Be("[A]\r\nk = v\r\n");
    }

    [Fact]
    public void Lines_the_parser_does_not_understand_are_written_back_untouched()
    {
        const string odd = "[A]\nthis line makes no sense\nk = v\n";

        IniDocument.Parse(odd).ToIniString().Should().Be(odd);
    }

    [Fact]
    public void Keys_before_the_first_section_belong_to_the_unnamed_section()
    {
        IniDocument document = IniDocument.Parse("version = 3\n\n[A]\nk = v\n");

        document.GetValue(string.Empty, "version").Should().Be("3");
        document.KeysIn("A").Should().Equal("k");
    }

    [Fact]
    public void Missing_keys_and_sections_are_silently_ignored_so_a_step_can_be_re_run()
    {
        IniDocument document = IniDocument.Parse(Sample);

        // Exactly what a retry after a restored snapshot looks like: the rename already
        // happened once, so the source key is gone.
        Action act = () => document
            .RenameKey("General", "notThere", "somethingElse")
            .RemoveKey("Nope", "alsoNotThere")
            .MoveKey("Nope", "x", "General")
            .ConvertValue("Nope", "x", value => value.ToUpperInvariant());

        act.Should().NotThrow();
        document.ToIniString().Should().Be(Sample);
    }

    [Fact]
    public async Task The_provider_migrates_the_file_and_restores_it_when_a_later_step_fails()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.ini", Sample);
        harness.StampVersion(new Version(1, 0, 0));

        IniMigrationProvider provider = new(
            "settings.ini",
            up: ini => ini.MoveKey("General", "fontSize", "Editor").SetDefault("Editor", "wordWrap", "true"),
            down: ini => ini.RemoveKey("Editor", "wordWrap").MoveKey("Editor", "fontSize", "General"));

        MigrationEngine engine = new(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Regroup the settings", provider)]);

        MigrationResult result = await engine.RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("settings.ini").Should().Contain("[Editor]").And.Contain("wordWrap = true");

        MigrationResult failed = await new MigrationEngine(
            harness.CreateOptions(options => options.TargetDataVersion = new Version(3, 0, 0)),
            [
                new MigrationStep(new Version(2, 5, 0), "Another INI change",
                    new IniMigrationProvider("settings.ini", ini => ini.Set("General", "language", "en"))),
                new MigrationStep(new Version(3, 0, 0), "Explodes",
                    new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("nope"))),
            ]).RunAsync();

        failed.Outcome.Should().Be(MigrationOutcome.Failed);
        harness.ReadFile("settings.ini").Should().Contain("language = vi", "the failed run was rolled back");
    }

    [Fact]
    public async Task The_provider_reverses_its_changes_on_a_downgrade()
    {
        using TestHarness harness = new();
        harness.WriteFile("settings.ini", Sample);
        harness.StampVersion(new Version(1, 0, 0));

        IniMigrationProvider provider = new(
            "settings.ini",
            up: ini => ini.MoveKey("General", "fontSize", "Editor", "fontSizePx"),
            down: ini => ini.MoveKey("Editor", "fontSizePx", "General", "fontSize"));

        MigrationStep step = new(new Version(2, 0, 0), "Regroup", provider);

        await new MigrationEngine(harness.CreateOptions(), [step]).RunAsync();
        harness.ReadFile("settings.ini").Should().Contain("fontSizePx = 14");

        MigrationResult down = await new MigrationEngine(
            harness.CreateOptions(options =>
            {
                options.TargetDataVersion = new Version(1, 0, 0);
                options.AllowDowngrade = true;
            }),
            [step]).RunAsync();

        down.Outcome.Should().Be(MigrationOutcome.Succeeded);

        IniDocument document = IniDocument.Parse(harness.ReadFile("settings.ini"));
        document.GetValue("General", "fontSize").Should().Be("14");
        document.ContainsKey("Editor", "fontSizePx").Should().BeFalse();
    }
}
