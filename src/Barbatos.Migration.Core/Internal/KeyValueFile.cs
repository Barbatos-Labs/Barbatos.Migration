// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration.Internal;

/// <summary>
/// A tiny <c>key=value</c> file reader/writer with an atomic write.
/// </summary>
/// <remarks>
/// <para>
/// The version stamp and the crash journal are the two files that must survive the exact
/// moment everything else is going wrong, so they get the simplest possible format: no
/// dependency to load, no schema to evolve, and readable in a text editor by a support
/// engineer looking at a user's machine. <c>System.Text.Json</c> would be nicer to write, but
/// it is a package reference on <c>netstandard2.0</c> - and it is precisely the kind of thing
/// a Unity IL2CPP build trips over.
/// </para>
/// <para>
/// Writes go to a sibling temporary file that is flushed to disk before being renamed over the
/// real one, so a reader never sees a half-written file.
/// </para>
/// </remarks>
internal static class KeyValueFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Dictionary<string, string>? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (string rawLine in File.ReadAllLines(path, Utf8NoBom))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line.Substring(0, separator).Trim();
            string value = Unescape(line.Substring(separator + 1).Trim());
            values[key] = value;
        }

        return values;
    }

    public static void Write(string path, IEnumerable<KeyValuePair<string, string>> values, string? header = null)
    {
        string directory = Path.GetDirectoryName(PathGuard.Normalize(path))!;
        Directory.CreateDirectory(directory);

        StringBuilder builder = new();
        if (!string.IsNullOrEmpty(header))
            builder.Append("# ").AppendLine(header);

        foreach (KeyValuePair<string, string> pair in values)
            builder.Append(pair.Key).Append('=').AppendLine(Escape(pair.Value));

        string temporary = path + ".tmp";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream, Utf8NoBom))
        {
            writer.Write(builder.ToString());
            writer.Flush();

            // Without this the rename below can land before the contents do: the metadata
            // update and the data blocks are not ordered by default, so a power cut can leave a
            // correctly named, zero-length journal.
            stream.Flush(flushToDisk: true);
        }

        Replace(temporary, path);
    }

    public static void Delete(string path)
    {
        TryDeleteFile(path);
        TryDeleteFile(path + ".tmp");
        TryDeleteFile(path + ".bak");
    }

    private static void Replace(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        string backup = destination + ".bak";
        try
        {
            // File.Replace is the only single-call atomic swap available on all the target
            // frameworks; it also leaves the previous contents in the backup file, which is
            // what makes an interrupted write recoverable.
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            TryDeleteFile(backup);
        }
        catch (PlatformNotSupportedException)
        {
            File.Delete(destination);
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Delete(destination);
            File.Move(source, destination);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        StringBuilder builder = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                builder.Append(value[i]);
                continue;
            }

            i++;
            builder.Append(value[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                '\\' => '\\',
                _ => value[i],
            });
        }

        return builder.ToString();
    }
}
