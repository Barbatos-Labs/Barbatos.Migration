// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration.Csv;

/// <summary>
/// An RFC 4180 reader.
/// </summary>
/// <remarks>
/// <para>
/// The rules that matter: a field may be quoted; a quoted field may contain the delimiter, line
/// breaks and quotes; a quote inside a quoted field is written twice. Everything else follows.
/// </para>
/// <para>
/// The one deliberate difference from a lenient reader is that a malformed file is
/// <b>rejected</b> rather than best-guessed. An unterminated quote makes every following line
/// part of one enormous field, and a migration that then rewrites the file has silently
/// destroyed the user's data - the failure mode this whole framework exists to prevent. Better
/// to fail the run with a line number and let the engine restore the snapshot.
/// </para>
/// </remarks>
internal static class CsvReader
{
    public static List<List<CsvField>> Read(string text, char delimiter)
    {
        List<List<CsvField>> records = [];
        List<CsvField> record = [];
        StringBuilder field = new();

        bool inQuotes = false;
        bool fieldWasQuoted = false;
        bool fieldStarted = false;
        int line = 1;
        int quoteOpenedAtLine = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote is an escaped quote; a single one closes the field.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = false;
                    continue;
                }

                if (c == '\n')
                    line++;

                field.Append(c);
                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldWasQuoted = true;
                fieldStarted = true;
                quoteOpenedAtLine = line;
                continue;
            }

            if (c == delimiter)
            {
                record.Add(new CsvField(field.ToString(), fieldWasQuoted));
                field.Clear();
                fieldWasQuoted = false;
                fieldStarted = false;
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                line++;

                record.Add(new CsvField(field.ToString(), fieldWasQuoted));
                records.Add(record);

                record = [];
                field.Clear();
                fieldWasQuoted = false;
                fieldStarted = false;
                continue;
            }

            field.Append(c);
            fieldStarted = true;
        }

        if (inQuotes)
        {
            throw new MigrationException(
                $"The CSV file is malformed: a quoted value opened on line {quoteOpenedAtLine} is never closed. " +
                "Migrating it would rewrite the rest of the file as a single value, so the run is being stopped instead.");
        }

        // A trailing newline leaves an empty pending record, which is not a row.
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(new CsvField(field.ToString(), fieldWasQuoted));
            records.Add(record);
        }

        return records;
    }
}
