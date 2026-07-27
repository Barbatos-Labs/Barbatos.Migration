// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration;

/// <summary>
/// Reads and writes a text file so that a crash can never leave it half-written.
/// </summary>
/// <remarks>
/// <para>
/// The provider packages all need the same thing: read a settings file, transform it, put it
/// back. The "put it back" is the dangerous half - a settings file truncated by a power cut is
/// a bricked application, and it is the cheapest possible thing to insure against. The write
/// goes to a sibling temporary file, is flushed all the way to the disk, and is only then
/// renamed over the original, so a reader never sees a partial document.
/// </para>
/// <para>
/// It is public because writing a custom provider needs exactly this primitive, and getting it
/// right from scratch involves knowing that <see cref="FileStream.Flush(bool)"/> and
/// <see cref="File.Replace(string, string, string)"/> both matter.
/// </para>
/// </remarks>
public static class AtomicFile
{
    private const string TemporarySuffix = ".migrating";
    private const string BackupSuffix = ".previous";

    /// <summary>UTF-8 without a byte-order mark - the default for a file this library creates.</summary>
    public static Encoding DefaultEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads a text file, returning both its contents and the encoding it was stored in, so a
    /// later <see cref="Write"/> can put it back the way the user's other tools expect it.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The contents and the detected encoding, or <see langword="null"/> when the file does not exist.</returns>
    public static TextFileContent? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return null;

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // detectEncodingFromByteOrderMarks only changes the reader's encoding when a BOM is
        // actually present, so a plain UTF-8 file still comes back as UTF-8 without one.
        using StreamReader reader = new(stream, DefaultEncoding, detectEncodingFromByteOrderMarks: true);

        string text = reader.ReadToEnd();
        return new TextFileContent(text, reader.CurrentEncoding);
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, replacing any existing
    /// file in one step.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">The text to write.</param>
    /// <param name="encoding">The encoding to use; defaults to <see cref="DefaultEncoding"/>.</param>
    public static void Write(string path, string contents, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory!);

        string temporary = full + TemporarySuffix;

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream, encoding ?? DefaultEncoding))
        {
            writer.Write(contents);
            writer.Flush();

            // Without this the rename below can land before the contents do: the metadata
            // update and the data blocks are not ordered by default, so a power cut can leave a
            // correctly named, zero-length file.
            stream.Flush(flushToDisk: true);
        }

        Replace(temporary, full);
    }

    private static void Replace(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        string backup = destination + BackupSuffix;

        try
        {
            // File.Replace is the only single-call atomic swap available across the target
            // frameworks, and it leaves the previous contents in the backup file - which is
            // what makes an interrupted write recoverable by hand if it ever comes to that.
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            TryDelete(backup);
            return;
        }
        catch (PlatformNotSupportedException)
        {
            // Some non-NTFS and network file systems do not implement it.
        }
        catch (IOException)
        {
        }

        File.Delete(destination);
        File.Move(source, destination);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort - a leftover .previous file is untidy, not incorrect.
        }
    }
}

/// <summary>The contents of a text file, together with the encoding it was stored in.</summary>
public sealed class TextFileContent
{
    internal TextFileContent(string text, Encoding encoding)
    {
        Text = text;
        Encoding = encoding;
    }

    /// <summary>The file's text.</summary>
    public string Text { get; }

    /// <summary>
    /// The encoding the file was stored in. Pass it back to <see cref="AtomicFile.Write"/> so a
    /// file another tool wrote as UTF-16 or with a BOM does not silently change format.
    /// </summary>
    public Encoding Encoding { get; }
}
