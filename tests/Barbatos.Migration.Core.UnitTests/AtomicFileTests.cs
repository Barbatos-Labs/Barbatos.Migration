// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.UnitTests;

/// <summary>
/// <see cref="AtomicFile"/> is the primitive every file-based provider writes through, and the
/// one a custom provider should use too - so its contract is worth pinning down directly rather
/// than only through the providers that happen to call it.
/// </summary>
public class AtomicFileTests
{
    [Fact]
    public void Reading_a_file_that_does_not_exist_returns_null()
    {
        using TestHarness harness = new();

        AtomicFile.Read(Path.Combine(harness.DataDirectory, "nope.txt")).Should().BeNull();
    }

    [Fact]
    public void A_written_file_round_trips()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "settings.txt");

        AtomicFile.Write(path, "xin chào\nthế giới");

        AtomicFile.Read(path)!.Text.Should().Be("xin chào\nthế giới");
    }

    [Fact]
    public void Writing_creates_missing_directories()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "deeply", "nested", "file.txt");

        AtomicFile.Write(path, "content");

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void The_default_encoding_is_utf8_without_a_byte_order_mark()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "plain.txt");

        AtomicFile.Write(path, "a");

        File.ReadAllBytes(path).Should().Equal((byte)'a');
    }

    [Fact]
    public void An_encoding_read_from_a_file_can_be_written_back_unchanged()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "bom.txt");

        // Another tool wrote it with a BOM. A migration that renames one key inside it must not
        // silently change the file's format for every other tool that reads it.
        File.WriteAllText(path, "original", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        TextFileContent read = AtomicFile.Read(path)!;
        read.Text.Should().Be("original");

        AtomicFile.Write(path, "rewritten", read.Encoding);

        File.ReadAllBytes(path).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        AtomicFile.Read(path)!.Text.Should().Be("rewritten");
    }

    [Fact]
    public void A_utf16_file_keeps_its_encoding()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "utf16.txt");
        File.WriteAllText(path, "nội dung", Encoding.Unicode);

        TextFileContent read = AtomicFile.Read(path)!;
        AtomicFile.Write(path, read.Text + "!", read.Encoding);

        File.ReadAllText(path, Encoding.Unicode).Should().Be("nội dung!");
    }

    [Fact]
    public void Overwriting_leaves_no_temporary_or_backup_files_behind()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "settings.txt");

        AtomicFile.Write(path, "first");
        AtomicFile.Write(path, "second");

        AtomicFile.Read(path)!.Text.Should().Be("second");
        Directory.GetFiles(harness.DataDirectory)
            .Select(Path.GetFileName)
            .Should().NotContain(name => name!.EndsWith(".migrating") || name.EndsWith(".previous"));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        using TestHarness harness = new();
        string path = Path.Combine(harness.DataDirectory, "x.txt");

        ((Action)(() => AtomicFile.Read(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => AtomicFile.Write(null!, "x"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => AtomicFile.Write(path, null!))).Should().Throw<ArgumentNullException>();
    }
}
