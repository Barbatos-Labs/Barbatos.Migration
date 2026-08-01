// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Migration.Csv;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// <see cref="CsvDocument.IndexOf"/> is backed by a cached name-to-position map, because a
/// migration over a large file resolves column names once per column per row. These pin the
/// behaviour that cache has to keep identical to the linear scan it replaced - above all that
/// every operation which reshapes the header drops it.
/// </summary>
public class CsvColumnIndexTests
{
    private const string Sample =
        "Id,FullName,Email\r\n" +
        "1,Pham The Hung,hung@example.com\r\n" +
        "2,Grace Hopper,grace@example.com\r\n";

    [Fact]
    public void Column_names_are_matched_case_insensitively()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.IndexOf("fullname").Should().Be(1);
        document.IndexOf("FULLNAME").Should().Be(1);
        document.Rows[0]["email"].Should().Be("hung@example.com");
    }

    [Fact]
    public void An_absent_column_reports_minus_one()
    {
        CsvDocument.Parse(Sample).IndexOf("Nope").Should().Be(-1);
    }

    [Fact]
    public void A_duplicated_header_name_resolves_to_the_leftmost_column()
    {
        CsvDocument document = CsvDocument.Parse("Name,Name\r\nfirst,second\r\n");

        document.IndexOf("Name").Should().Be(0);
        document.Rows[0]["Name"].Should().Be("first");
    }

    [Fact]
    public void Renaming_a_column_moves_the_name_without_moving_the_values()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["FullName"].Should().Be("Pham The Hung");

        document.RenameColumn("FullName", "DisplayName");

        document.IndexOf("FullName").Should().Be(-1);
        document.IndexOf("DisplayName").Should().Be(1);
        document.Rows[0]["DisplayName"].Should().Be("Pham The Hung");
    }

    [Fact]
    public void Adding_a_column_shifts_the_ones_after_it()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["Email"].Should().Be("hung@example.com");

        document.AddColumn("Nickname", "n/a", index: 1);

        document.IndexOf("Nickname").Should().Be(1);
        document.IndexOf("FullName").Should().Be(2);
        document.Rows[0]["Email"].Should().Be("hung@example.com");
        document.Rows[0]["Nickname"].Should().Be("n/a");
    }

    [Fact]
    public void Removing_a_column_shifts_the_ones_after_it()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["Email"].Should().Be("hung@example.com");

        document.RemoveColumn("FullName");

        document.IndexOf("FullName").Should().Be(-1);
        document.IndexOf("Email").Should().Be(1);
        document.Rows[0]["Email"].Should().Be("hung@example.com");
    }

    [Fact]
    public void Moving_a_column_takes_its_values_with_it()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["Email"].Should().Be("hung@example.com");

        document.MoveColumn("Email", 0);

        document.IndexOf("Email").Should().Be(0);
        document.IndexOf("Id").Should().Be(1);
        document.Rows[0]["Email"].Should().Be("hung@example.com");
        document.Rows[0]["Id"].Should().Be("1");
    }

    [Fact]
    public void Splitting_a_column_resolves_the_new_names()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["FullName"].Should().Be("Pham The Hung");

        document.SplitColumn("FullName", value => value.Split(' '), "First", "Last");

        document.IndexOf("FullName").Should().Be(-1);
        document.Rows[0]["First"].Should().Be("Pham");
        document.Rows[0]["Last"].Should().Be("The");
    }

    [Fact]
    public void Merging_columns_resolves_the_target_name()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.Rows[0]["Id"].Should().Be("1");

        document.MergeColumns("Contact", parts => string.Join(" <", parts) + ">", "FullName", "Email");

        document.IndexOf("FullName").Should().Be(-1);
        document.IndexOf("Email").Should().Be(-1);
        document.Rows[0]["Contact"].Should().Be("Pham The Hung <hung@example.com>");
    }

    [Fact]
    public void A_document_created_from_scratch_resolves_its_columns()
    {
        CsvDocument document = CsvDocument.Create(["Id", "Name"]);
        document.AddRow(new KeyValuePair<string, string>[] { new("Name", "Ada") });

        document.IndexOf("Name").Should().Be(1);
        document.Rows[0]["Name"].Should().Be("Ada");
    }

    [Fact]
    public void Writing_to_a_column_removed_earlier_in_the_same_run_is_rejected()
    {
        CsvDocument document = CsvDocument.Parse(Sample);
        document.RemoveColumn("Email");

        Action act = () => document.Rows[0]["Email"] = "x@example.com";

        act.Should().Throw<MigrationException>().WithMessage("*no column called 'Email'*");
    }

    [Fact]
    public void An_empty_file_renders_back_as_an_empty_file()
    {
        // A data file with no records yet is an ordinary thing to find. Giving it a line break
        // would mean a migration that changed nothing about it still edited it.
        CsvDocument.Parse("").ToCsvString().Should().BeEmpty();
        CsvDocument.Parse("", hasHeader: false).ToCsvString().Should().BeEmpty();
    }

    [Fact]
    public void A_header_with_no_rows_keeps_its_header()
    {
        CsvDocument.Parse("Id,Name\r\n").ToCsvString().Should().Be("Id,Name\r\n");
        CsvDocument.Parse("Id,Name").ToCsvString().Should().Be("Id,Name");
    }

    [Fact]
    public void A_headerless_file_does_not_gain_a_leading_line_break()
    {
        CsvDocument.Parse("1,2\r\n3,4\r\n", hasHeader: false).ToCsvString()
            .Should().Be("1,2\r\n3,4\r\n");
    }

    [Fact]
    public void A_document_built_from_scratch_still_writes_its_header()
    {
        CsvDocument document = CsvDocument.Create(["Id", "Name"], ',');
        document.NewLine = "\r\n";
        document.AddRow("1", "Ada");

        document.ToCsvString().Should().Be("Id,Name\r\n1,Ada\r\n");
    }
}
