// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration.Csv;

/// <summary>How values are quoted when the document is written back.</summary>
public enum CsvQuoteStyle
{
    /// <summary>
    /// Quote a value only when it has to be - it contains the delimiter, a quote or a line
    /// break. The default for a document built from scratch.
    /// </summary>
    Minimal,

    /// <summary>
    /// Quote a value if it was quoted in the original file, or if it now has to be. The default
    /// when parsing, so a file whose author quoted every text column keeps looking that way.
    /// </summary>
    PreserveOriginal,

    /// <summary>Quote every value.</summary>
    All,
}

/// <summary>
/// A delimited data file - CSV, TSV, semicolon-separated - as an editable table.
/// </summary>
/// <remarks>
/// <para>
/// CSV files in an application's data folder are tables of real records: exported data, licence
/// lists, index files, saved reports. Migrating one means the same kind of work as migrating a
/// database table - add a column, split a column into two, drop one nobody reads any more - and
/// the same requirement that a file the user may also open in Excel comes back looking like the
/// file they had.
/// </para>
/// <para>
/// So the delimiter, the quoting style, the line ending and the presence or absence of a header
/// row are all detected on the way in and reproduced on the way out. Only the cells a migration
/// actually changes are different afterwards.
/// </para>
/// <para>
/// Parsing follows RFC 4180: doubled quotes inside a quoted field are an escaped quote, and a
/// quoted field may contain the delimiter and line breaks. A file that breaks those rules -
/// most commonly an unterminated quote - is reported with the line number rather than silently
/// mangled, because half-parsing someone's data is worse than refusing it.
/// </para>
/// </remarks>
public sealed class CsvDocument
{
    private static readonly char[] CandidateDelimiters = [',', ';', '\t', '|'];

    private readonly List<string> _columns = [];
    private readonly List<CsvRow> _rows = [];

    private CsvDocument(char delimiter, string newLine, bool hasHeader)
    {
        Delimiter = delimiter;
        NewLine = newLine;
        HasHeader = hasHeader;
    }

    /// <summary>The field separator. Detected when parsing.</summary>
    public char Delimiter { get; set; }

    /// <summary>The line ending. Detected when parsing, so the file does not change wholesale on first touch.</summary>
    public string NewLine { get; set; }

    /// <summary>Whether the first row is a header. Column operations require it.</summary>
    public bool HasHeader { get; set; }

    /// <summary>Whether the file ends with a trailing newline. Preserved from the original.</summary>
    public bool EndsWithNewLine { get; set; } = true;

    /// <summary>How values are quoted on the way out.</summary>
    public CsvQuoteStyle QuoteStyle { get; set; } = CsvQuoteStyle.PreserveOriginal;

    /// <summary>The column names, in order. Empty when the file has no header.</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>The data rows, excluding the header.</summary>
    public IReadOnlyList<CsvRow> Rows => _rows;

    /// <summary>Creates an empty document.</summary>
    /// <param name="columns">The column names.</param>
    /// <param name="delimiter">The field separator.</param>
    public static CsvDocument Create(IEnumerable<string> columns, char delimiter = ',')
    {
        CsvDocument document = new(delimiter, Environment.NewLine, hasHeader: true)
        {
            QuoteStyle = CsvQuoteStyle.Minimal,
        };

        document._columns.AddRange(columns ?? throw new ArgumentNullException(nameof(columns)));
        return document;
    }

    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <param name="text">The file contents.</param>
    /// <param name="hasHeader">Whether the first row is a header. Defaults to <see langword="true"/>.</param>
    /// <param name="delimiter">The field separator, or <see langword="null"/> to detect it.</param>
    /// <exception cref="MigrationException">The file is not well-formed CSV.</exception>
    public static CsvDocument Parse(string text, bool hasHeader = true, char? delimiter = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        char separator = delimiter ?? DetectDelimiter(text);
        CsvDocument document = new(separator, DetectNewLine(text), hasHeader)
        {
            EndsWithNewLine = text.Length == 0 || text[text.Length - 1] is '\n' or '\r',
        };

        List<List<CsvField>> records = CsvReader.Read(text, separator);
        if (records.Count == 0)
            return document;

        int start = 0;
        if (hasHeader)
        {
            document._columns.AddRange(records[0].Select(field => field.Value));
            start = 1;
        }

        for (int i = start; i < records.Count; i++)
            document._rows.Add(new CsvRow(document, records[i]));

        return document;
    }

    /// <summary>Renders the document back to text.</summary>
    public string ToCsvString()
    {
        StringBuilder builder = new();
        bool first = true;

        if (HasHeader)
        {
            AppendRecord(builder, _columns.Select(name => new CsvField(name, wasQuoted: false)));
            first = false;
        }

        foreach (CsvRow row in _rows)
        {
            if (!first)
                builder.Append(NewLine);

            AppendRecord(builder, row.Fields);
            first = false;
        }

        if (EndsWithNewLine && (!first || HasHeader))
            builder.Append(NewLine);

        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToCsvString();

    // ---------------------------------------------------------------- columns

    /// <summary>The index of <paramref name="column"/>, or <c>-1</c> when it is absent.</summary>
    public int IndexOf(string column) =>
        _columns.FindIndex(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the document has a column called <paramref name="column"/>.</summary>
    public bool ContainsColumn(string column) => IndexOf(column) >= 0;

    /// <summary>
    /// Adds a column, filling every existing row from <paramref name="value"/>.
    /// </summary>
    /// <param name="column">The new column's name.</param>
    /// <param name="value">Computes the value for a row. <see langword="null"/> fills with the empty string.</param>
    /// <param name="index">Where to put it, or <see langword="null"/> to append.</param>
    public CsvDocument AddColumn(string column, Func<CsvRow, string>? value = null, int? index = null)
    {
        RequireHeader();
        Require(column, nameof(column));

        if (ContainsColumn(column))
            return this;

        int at = index ?? _columns.Count;
        at = at < 0 ? 0 : at > _columns.Count ? _columns.Count : at;

        _columns.Insert(at, column);

        foreach (CsvRow row in _rows)
            row.InsertField(at, value == null ? string.Empty : value(row));

        return this;
    }

    /// <inheritdoc cref="AddColumn(string, Func{CsvRow, string}, int?)"/>
    public CsvDocument AddColumn(string column, string defaultValue, int? index = null) =>
        AddColumn(column, _ => defaultValue, index);

    /// <summary>Removes a column and its values from every row.</summary>
    public CsvDocument RemoveColumn(string column)
    {
        RequireHeader();

        int at = IndexOf(column);
        if (at < 0)
            return this;

        _columns.RemoveAt(at);
        foreach (CsvRow row in _rows)
            row.RemoveField(at);

        return this;
    }

    /// <summary>Renames a column, keeping its position and every value under it.</summary>
    public CsvDocument RenameColumn(string from, string to)
    {
        RequireHeader();
        Require(to, nameof(to));

        int at = IndexOf(from);
        if (at >= 0)
            _columns[at] = to;

        return this;
    }

    /// <summary>Moves a column to a new position, taking its values with it.</summary>
    public CsvDocument MoveColumn(string column, int index)
    {
        RequireHeader();

        int from = IndexOf(column);
        if (from < 0)
            return this;

        int to = index < 0 ? 0 : index >= _columns.Count ? _columns.Count - 1 : index;
        if (to == from)
            return this;

        string name = _columns[from];
        _columns.RemoveAt(from);
        _columns.Insert(to, name);

        foreach (CsvRow row in _rows)
            row.MoveField(from, to);

        return this;
    }

    /// <summary>Rewrites every value in a column - for changes of unit, format or encoding.</summary>
    public CsvDocument TransformColumn(string column, Func<string, string> convert)
    {
        ArgumentNullException.ThrowIfNull(convert);

        RequireHeader();

        int at = IndexOf(column);
        if (at < 0)
            return this;

        foreach (CsvRow row in _rows)
            row.SetField(at, convert(row.GetField(at)));

        return this;
    }

    /// <summary>
    /// Splits one column into several - the table equivalent of splitting a full name into a
    /// first and last name.
    /// </summary>
    /// <param name="source">The column to split. Removed unless it is named in <paramref name="targets"/>.</param>
    /// <param name="split">Produces one value per entry in <paramref name="targets"/>.</param>
    /// <param name="targets">The new column names, in order.</param>
    public CsvDocument SplitColumn(string source, Func<string, IReadOnlyList<string>> split, params string[] targets)
    {
        ArgumentNullException.ThrowIfNull(split);
        if (targets == null || targets.Length == 0)
            throw new ArgumentException("At least one target column is required.", nameof(targets));

        RequireHeader();

        int at = IndexOf(source);
        if (at < 0)
            return this;

        // Values are computed before the shape changes, so `split` always sees the original.
        List<IReadOnlyList<string>> values = _rows.Select(row => split(row.GetField(at))).ToList();

        for (int t = 0; t < targets.Length; t++)
        {
            string target = targets[t];
            int targetIndex = at + t;

            if (ContainsColumn(target))
            {
                int existing = IndexOf(target);
                for (int r = 0; r < _rows.Count; r++)
                    _rows[r].SetField(existing, Value(values[r], t));

                continue;
            }

            _columns.Insert(targetIndex, target);
            for (int r = 0; r < _rows.Count; r++)
                _rows[r].InsertField(targetIndex, Value(values[r], t));
        }

        if (!targets.Contains(source, StringComparer.OrdinalIgnoreCase))
            RemoveColumn(source);

        return this;

        static string Value(IReadOnlyList<string> parts, int index) =>
            index < parts.Count ? parts[index] : string.Empty;
    }

    /// <summary>
    /// Merges several columns into one - the inverse of <see cref="SplitColumn"/>.
    /// </summary>
    /// <param name="target">The column to write into, created if needed.</param>
    /// <param name="merge">Combines the source values, in the order given.</param>
    /// <param name="sources">The columns to read. Removed afterwards unless one of them is <paramref name="target"/>.</param>
    public CsvDocument MergeColumns(string target, Func<IReadOnlyList<string>, string> merge, params string[] sources)
    {
        ArgumentNullException.ThrowIfNull(merge);
        if (sources == null || sources.Length == 0)
            throw new ArgumentException("At least one source column is required.", nameof(sources));

        RequireHeader();

        int[] indexes = sources.Select(IndexOf).ToArray();
        if (indexes.Any(index => index < 0))
            return this;

        List<string> merged = _rows
            .Select(row => merge(indexes.Select(row.GetField).ToList()))
            .ToList();

        if (!ContainsColumn(target))
            AddColumn(target, _ => string.Empty, indexes[0]);

        int targetIndex = IndexOf(target);
        for (int r = 0; r < _rows.Count; r++)
            _rows[r].SetField(targetIndex, merged[r]);

        foreach (string source in sources)
        {
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                RemoveColumn(source);
        }

        return this;
    }

    // ---------------------------------------------------------------- rows

    /// <summary>Appends a row from column name/value pairs. Unlisted columns are left empty.</summary>
    public CsvRow AddRow(IEnumerable<KeyValuePair<string, string>> values)
    {
        RequireHeader();

        CsvRow row = new(this, _columns.Select(_ => new CsvField(string.Empty, wasQuoted: false)).ToList());

        foreach (KeyValuePair<string, string> pair in values ?? [])
            row[pair.Key] = pair.Value;

        _rows.Add(row);
        return row;
    }

    /// <summary>Appends a row from positional values.</summary>
    public CsvRow AddRow(params string[] values)
    {
        CsvRow row = new(this, (values ?? []).Select(value => new CsvField(value, wasQuoted: false)).ToList());
        _rows.Add(row);
        return row;
    }

    /// <summary>Removes every row matching <paramref name="predicate"/>, and returns how many went.</summary>
    public int RemoveRows(Func<CsvRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return _rows.RemoveAll(row => predicate(row));
    }

    /// <summary>
    /// Applies <paramref name="update"/> to every row, reporting progress as it goes - a data
    /// file with a hundred thousand rows is the case this exists for.
    /// </summary>
    public CsvDocument UpdateRows(
        Action<CsvRow> update,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        for (int i = 0; i < _rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            update(_rows[i]);

            // Reporting every row on a large file would drown the UI, and the engine's relay
            // would throttle most of it away anyway.
            if (progress != null && (i % 500 == 0 || i == _rows.Count - 1))
                progress.Report(new MigrationProgress(i * 100.0 / _rows.Count, $"Row {i + 1} of {_rows.Count}"));
        }

        return this;
    }

    // ---------------------------------------------------------------- internals

    internal void RequireHeader()
    {
        if (!HasHeader)
        {
            throw new MigrationException(
                "This CSV document has no header row, so columns cannot be addressed by name. " +
                "Parse it with hasHeader: true, or use the positional CsvRow indexer.");
        }
    }

    private void AppendRecord(StringBuilder builder, IEnumerable<CsvField> fields)
    {
        bool first = true;

        foreach (CsvField field in fields)
        {
            if (!first)
                builder.Append(Delimiter);

            builder.Append(Quote(field));
            first = false;
        }
    }

    private string Quote(CsvField field)
    {
        string value = field.Value;

        bool mustQuote = value.IndexOf(Delimiter) >= 0
            || value.IndexOf('"') >= 0
            || value.IndexOf('\n') >= 0
            || value.IndexOf('\r') >= 0;

        bool quote = QuoteStyle switch
        {
            CsvQuoteStyle.All => true,
            CsvQuoteStyle.PreserveOriginal => mustQuote || field.WasQuoted,
            _ => mustQuote,
        };

        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A column name is required.", parameterName);
    }

    /// <summary>
    /// Picks the delimiter by counting candidates outside quoted fields on the first line. A
    /// wrong guess would turn one column into several, so ties fall back to a comma.
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        string firstLine = end < 0 ? text : text.Substring(0, end);

        char best = ',';
        int bestCount = 0;
        bool inQuotes = false;

        Dictionary<char, int> counts = CandidateDelimiters.ToDictionary(c => c, _ => 0);

        foreach (char c in firstLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && counts.ContainsKey(c))
                counts[c]++;
        }

        foreach (char candidate in CandidateDelimiters)
        {
            if (counts[candidate] > bestCount)
            {
                best = candidate;
                bestCount = counts[candidate];
            }
        }

        return best;
    }

    private static string DetectNewLine(string text)
    {
        int index = text.IndexOf('\n');
        if (index < 0)
            return Environment.NewLine;

        return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }
}
