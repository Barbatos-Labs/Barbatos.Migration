// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Migration.Ini;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// INI files as they actually turn up in a shipped application: colons in section and key names,
/// quoted values, comments that document the section below them, values containing the separator,
/// and Vietnamese text. Modelled on the localisation files in Barbatos.i18n, which is the closest
/// thing to hand to a real-world INI a Barbatos application would already have on disk.
/// </summary>
public class ComplexIniTests
{
    private const string Complex =
        "; Cau hinh ung dung - nguoi dung tu sua tay\r\n" +
        "schemaVersion = 1\r\n" +
        "\r\n" +
        "[User]\r\n" +
        "FullName=\"Pham The Hung\"\r\n" +
        "Address=\"So 1, Duong Lang; Ha Noi\"\r\n" +
        "\r\n" +
        "[Messages:Error]\r\n" +
        "InvalidEmail=\"Dia chi email khong hop le.\"\r\n" +
        "Required=\"Truong nay la bat buoc.\"\r\n" +
        "\r\n" +
        "; Ghi chu cho phan Advanced\r\n" +
        "[Advanced]\r\n" +
        "Timeout:Max=30 ; tinh bang giay\r\n" +
        "ConnectionString=Server=localhost;Db=app\r\n" +
        "EmptyValue=\r\n" +
        "  Indented   =   spaced out   \r\n";

    [Fact]
    public void The_whole_document_round_trips_byte_for_byte()
    {
        IniDocument.Parse(Complex).ToIniString().Should().Be(Complex);
    }

    [Fact]
    public void A_section_name_containing_a_colon_is_addressable()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.ContainsSection("Messages:Error").Should().BeTrue();
        document.GetValue("Messages:Error", "Required").Should().Be("Truong nay la bat buoc.");
        document.SectionNames.Should().Equal(["User", "Messages:Error", "Advanced"]);
    }

    [Fact]
    public void A_key_name_containing_a_colon_keeps_its_trailing_comment_when_renamed()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.GetValue("Advanced", "Timeout:Max").Should().Be("30");

        document.RenameKey("Advanced", "Timeout:Max", "Timeout:MaxSeconds");

        document.ToIniString().Should().Contain("Timeout:MaxSeconds=30 ; tinh bang giay");
    }

    [Fact]
    public void A_value_containing_the_separator_is_read_whole()
    {
        IniDocument document = IniDocument.Parse(Complex);

        // Only the first '=' separates key from value; the rest belongs to the value.
        document.GetValue("Advanced", "ConnectionString").Should().Be("Server=localhost;Db=app");
    }

    [Fact]
    public void A_quoted_value_containing_a_semicolon_is_not_mistaken_for_a_comment()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.GetValue("User", "Address").Should().Be("So 1, Duong Lang; Ha Noi");
    }

    [Fact]
    public void A_quoted_value_stays_quoted_when_it_is_changed()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.Set("User", "FullName", "Nguyen Van A");

        document.ToIniString().Should().Contain("FullName=\"Nguyen Van A\"");
    }

    [Fact]
    public void An_empty_value_reads_as_empty_rather_than_null()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.ContainsKey("Advanced", "EmptyValue").Should().BeTrue();
        document.GetValue("Advanced", "EmptyValue").Should().BeEmpty();
    }

    [Fact]
    public void Indentation_and_spacing_around_the_separator_survive_a_value_change()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.GetValue("Advanced", "Indented").Should().Be("spaced out");

        document.Set("Advanced", "Indented", "still spaced");

        document.ToIniString().Should().Contain("  Indented   =   still spaced   ");
    }

    [Fact]
    public void Keys_above_the_first_section_belong_to_the_unnamed_section()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.GetValue(string.Empty, "schemaVersion").Should().Be("1");
        document.KeysIn(string.Empty).Should().Equal(["schemaVersion"]);
    }

    [Fact]
    public void Removing_a_section_keeps_the_comment_that_documents_the_next_one()
    {
        IniDocument document = IniDocument.Parse(Complex);

        // "; Ghi chu cho phan Advanced" sits between [Messages:Error] and [Advanced]. It reads
        // as documentation for Advanced, so deleting Messages:Error must not take it away.
        document.RemoveSection("Messages:Error");

        string result = document.ToIniString();
        result.Should().NotContain("[Messages:Error]").And.NotContain("InvalidEmail");
        result.Should().Contain("; Ghi chu cho phan Advanced\r\n[Advanced]");
        result.Should().Contain("; Cau hinh ung dung", "the file's own header is untouched");
    }

    [Fact]
    public void Moving_a_key_into_a_new_section_keeps_its_quoting()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.MoveKey("User", "Address", "Contact");

        document.GetValue("Contact", "Address").Should().Be("So 1, Duong Lang; Ha Noi");
        document.ToIniString().Should().Contain("Address=\"So 1, Duong Lang; Ha Noi\"");
    }

    [Fact]
    public void Renaming_a_section_moves_every_key_under_the_new_name()
    {
        IniDocument document = IniDocument.Parse(Complex);

        document.RenameSection("Messages:Error", "Messages:Validation");

        document.KeysIn("Messages:Validation").Should().Equal(["InvalidEmail", "Required"]);
        document.ToIniString().Should().Contain("[Messages:Validation]");
    }

    [Fact]
    public void A_file_with_no_trailing_newline_does_not_gain_one()
    {
        const string noNewline = "[A]\r\nk = v";

        IniDocument.Parse(noNewline).ToIniString().Should().Be(noNewline);
    }

    [Fact]
    public void Vietnamese_text_survives_a_round_trip()
    {
        const string vietnamese = "[Greeting]\r\nText = Xin chào, mừng bạn quay lại!\r\n";

        IniDocument document = IniDocument.Parse(vietnamese);
        document.GetValue("Greeting", "Text").Should().Be("Xin chào, mừng bạn quay lại!");
        document.ToIniString().Should().Be(vietnamese);
    }

    [Fact]
    public void A_placeholder_in_a_value_is_not_interpreted()
    {
        IniDocument document = IniDocument.Parse("[Format]\r\nPrice = Giá bán: {0:C}\r\n");

        document.GetValue("Format", "Price").Should().Be("Giá bán: {0:C}");
    }

    [Fact]
    public void Case_insensitivity_is_the_default_and_can_be_turned_off()
    {
        IniDocument insensitive = IniDocument.Parse(Complex);
        insensitive.GetValue("user", "fullname").Should().Be("Pham The Hung");

        IniDocument sensitive = IniDocument.Parse(Complex, caseSensitive: true);
        sensitive.GetValue("user", "fullname").Should().BeNull();
        sensitive.GetValue("User", "FullName").Should().Be("Pham The Hung");
    }

    [Fact]
    public async Task A_complex_file_migrates_and_rolls_back_intact()
    {
        using TestHarness harness = new();
        harness.WriteFile("config.ini", Complex);
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(options => options.TargetDataVersion = new Version(3, 0, 0)),
            [
                new MigrationStep(new Version(2, 0, 0), "Regroup",
                    new IniMigrationProvider("config.ini", ini => ini
                        .RenameSection("Messages:Error", "Messages:Validation")
                        .MoveKey("User", "Address", "Contact")
                        .ConvertValue("Advanced", "Timeout:Max", seconds => $"{int.Parse(seconds) * 1000}"))),
                new MigrationStep(new Version(3, 0, 0), "Explodes",
                    new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("nope"))),
            ]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Failed);
        harness.ReadFile("config.ini").Should().Be(Complex, "the whole file came back, comments and all");
    }
}
