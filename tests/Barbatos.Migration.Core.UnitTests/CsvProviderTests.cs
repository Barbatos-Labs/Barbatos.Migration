// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Linq;
using AwesomeAssertions;
using Barbatos.Migration.Csv;
using Xunit;

namespace Barbatos.Migration.UnitTests;

public class CsvProviderTests
{
    private const string Sample =
        "Id,FullName,Email\r\n" +
        "1,Pham The Hung,hung@example.com\r\n" +
        "2,\"Lovelace, Ada\",ada@example.com\r\n" +
        "3,Grace Hopper,grace@example.com\r\n";

    [Fact]
    public void An_untouched_document_round_trips_byte_for_byte()
    {
        CsvDocument.Parse(Sample).ToCsvString().Should().Be(Sample);
    }

    [Fact]
    public void A_quoted_value_containing_the_delimiter_survives_the_round_trip()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.Rows[1]["FullName"].Should().Be("Lovelace, Ada");
        document.ToCsvString().Should().Contain("\"Lovelace, Ada\"");
    }

    [Fact]
    public void Escaped_quotes_are_read_and_written_back_correctly()
    {
        CsvDocument document = CsvDocument.Parse("Name\r\n\"He said \"\"hi\"\"\"\r\n");

        document.Rows[0]["Name"].Should().Be("He said \"hi\"");
        document.ToCsvString().Should().Be("Name\r\n\"He said \"\"hi\"\"\"\r\n");
    }

    [Fact]
    public void A_quoted_value_may_span_lines()
    {
        CsvDocument document = CsvDocument.Parse("Id,Note\n1,\"first line\nsecond line\"\n");

        document.Rows.Should().ContainSingle();
        document.Rows[0]["Note"].Should().Be("first line\nsecond line");
    }

    [Fact]
    public void An_unterminated_quote_is_rejected_rather_than_swallowing_the_rest_of_the_file()
    {
        Action act = () => CsvDocument.Parse("Id,Name\n1,\"never closed\n2,Ada\n");

        act.Should().Throw<MigrationException>()
            .WithMessage("*never closed*")
            .WithMessage("*line 2*");
    }

    [Theory]
    [InlineData("a,b\n1,2\n", ',')]
    [InlineData("a;b\n1;2\n", ';')]
    [InlineData("a\tb\n1\t2\n", '\t')]
    [InlineData("a|b\n1|2\n", '|')]
    public void The_delimiter_is_detected_from_the_header(string text, char expected)
    {
        CsvDocument document = CsvDocument.Parse(text);

        document.Delimiter.Should().Be(expected);
        document.Columns.Should().Equal("a", "b");
        document.ToCsvString().Should().Be(text);
    }

    [Fact]
    public void Adding_a_column_fills_every_existing_row()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.AddColumn("Archived", "false");

        document.Columns.Should().Equal("Id", "FullName", "Email", "Archived");
        document.Rows.Select(row => row["Archived"]).Should().AllBe("false");
    }

    [Fact]
    public void Adding_a_column_can_compute_its_value_from_the_row()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.AddColumn("Domain", row => row["Email"].Split('@')[1]);

        document.Rows.Select(row => row["Domain"]).Should().AllBe("example.com");
    }

    [Fact]
    public void Renaming_a_column_keeps_its_position_and_its_values()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.RenameColumn("Email", "EmailAddress");

        document.Columns.Should().Equal("Id", "FullName", "EmailAddress");
        document.Rows[0]["EmailAddress"].Should().Be("hung@example.com");
    }

    [Fact]
    public void Removing_a_column_takes_its_values_with_it()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.RemoveColumn("Email");

        document.Columns.Should().Equal("Id", "FullName");
        document.ToCsvString().Should().NotContain("example.com");
        document.ToCsvString().Should().StartWith("Id,FullName\r\n1,Pham The Hung\r\n");
    }

    [Fact]
    public void Splitting_a_column_puts_the_new_ones_where_the_old_one_was()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.SplitColumn(
            "FullName",
            name => name.Split(' ', 2),
            "FirstName",
            "LastName");

        document.Columns.Should().Equal("Id", "FirstName", "LastName", "Email");
        document.Rows[0]["FirstName"].Should().Be("Pham");
        document.Rows[0]["LastName"].Should().Be("The Hung");
        document.Rows[2]["LastName"].Should().Be("Hopper");
    }

    [Fact]
    public void Merging_columns_is_the_inverse_of_splitting_them()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.SplitColumn("FullName", name => name.Split(' ', 2), "FirstName", "LastName");
        document.MergeColumns("FullName", parts => string.Join(" ", parts), "FirstName", "LastName");

        document.Columns.Should().Equal("Id", "FullName", "Email");
        document.Rows.Select(row => row["FullName"])
            .Should().Equal("Pham The Hung", "Lovelace, Ada", "Grace Hopper");
    }

    [Fact]
    public void Transforming_a_column_rewrites_every_value_in_it()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.TransformColumn("Email", value => value.ToUpperInvariant());

        document.Rows[0]["Email"].Should().Be("HUNG@EXAMPLE.COM");
    }

    [Fact]
    public void Rows_can_be_added_and_removed()
    {
        CsvDocument document = CsvDocument.Parse(Sample);

        document.AddRow([new KeyValuePair<string, string>("Id", "4"), new KeyValuePair<string, string>("FullName", "Alan Turing")]);
        document.Rows.Should().HaveCount(4);
        document.Rows[3]["Email"].Should().BeEmpty();

        document.RemoveRows(row => row["Id"] == "2").Should().Be(1);
        document.Rows.Select(row => row["Id"]).Should().Equal("1", "3", "4");
    }

    [Fact]
    public void UpdateRows_reports_progress_and_honours_cancellation()
    {
        CsvDocument document = CsvDocument.Create(["Id"]);
        for (int i = 0; i < 1200; i++)
            document.AddRow(i.ToString());

        List<double> reports = [];
        document.UpdateRows(row => row["Id"] = "x", new SyncProgress(p => reports.Add(p.Percentage)));

        reports.Should().NotBeEmpty();
        reports.Should().BeInAscendingOrder();

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Action act = () => document.UpdateRows(_ => { }, null, cancellation.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_ragged_row_reads_as_empty_rather_than_throwing()
    {
        CsvDocument document = CsvDocument.Parse("Id,Name,Email\n1,Ada\n");

        document.Rows[0]["Email"].Should().BeEmpty();

        document.Rows[0]["Email"] = "ada@example.com";
        document.ToCsvString().Should().Contain("1,Ada,ada@example.com");
    }

    [Fact]
    public void A_file_with_no_header_is_addressed_positionally()
    {
        CsvDocument document = CsvDocument.Parse("1,Ada\n2,Grace\n", hasHeader: false);

        document.Rows.Should().HaveCount(2);
        document.Rows[1][1].Should().Be("Grace");

        Action act = () => document.AddColumn("Extra", "x");
        act.Should().Throw<MigrationException>().WithMessage("*no header row*");
    }

    [Fact]
    public async Task The_provider_migrates_the_file_and_restores_it_when_the_step_fails()
    {
        using TestHarness harness = new();
        harness.WriteFile("users.csv", Sample);
        harness.StampVersion(new Version(1, 0, 0));

        CsvMigrationProvider provider = new(
            "users.csv",
            up: csv => csv.SplitColumn("FullName", name => name.Split(' ', 2), "FirstName", "LastName"),
            down: csv => csv.MergeColumns("FullName", parts => string.Join(" ", parts), "FirstName", "LastName"));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Split the name column", provider)]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.ReadFile("users.csv").Should().StartWith("Id,FirstName,LastName,Email");

        MigrationResult failed = await new MigrationEngine(
            harness.CreateOptions(options => options.TargetDataVersion = new Version(3, 0, 0)),
            [
                new MigrationStep(new Version(2, 5, 0), "Add a column",
                    new CsvMigrationProvider("users.csv", csv => csv.AddColumn("Archived", "false"))),
                new MigrationStep(new Version(3, 0, 0), "Explodes",
                    new RecordingProvider("boom", (_, _) => throw new InvalidOperationException("nope"))),
            ]).RunAsync();

        failed.Outcome.Should().Be(MigrationOutcome.Failed);
        harness.ReadFile("users.csv").Should().NotContain("Archived", "the failed run was rolled back");
    }

    [Fact]
    public async Task A_missing_data_file_is_skipped_rather_than_invented()
    {
        using TestHarness harness = new();
        harness.StampVersion(new Version(1, 0, 0));

        MigrationResult result = await new MigrationEngine(
            harness.CreateOptions(),
            [new MigrationStep(new Version(2, 0, 0), "Add a column",
                new CsvMigrationProvider("absent.csv", csv => csv.AddColumn("X", "1")))]).RunAsync();

        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
        harness.FileExists("absent.csv").Should().BeFalse();
    }

    private sealed class SyncProgress(Action<MigrationProgress> onReport) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => onReport(value);
    }
}
