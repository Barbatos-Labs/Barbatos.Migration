// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.


namespace Barbatos.Migration.Csv;

/// <summary>One field, and whether it was quoted in the file it came from.</summary>
internal readonly struct CsvField
{
    public CsvField(string value, bool wasQuoted)
    {
        Value = value;
        WasQuoted = wasQuoted;
    }

    public string Value { get; }

    /// <summary>
    /// Remembered so <see cref="CsvQuoteStyle.PreserveOriginal"/> can put the quotes back. A
    /// file whose author quoted every text column should still look that way afterwards.
    /// </summary>
    public bool WasQuoted { get; }
}

/// <summary>
/// One data row. Address fields by column name when the file has a header, or by position when
/// it does not.
/// </summary>
/// <remarks>
/// A row can legitimately be shorter or longer than the header - real exported files are ragged
/// more often than anyone would like. Reading a missing field gives the empty string, and
/// writing one pads the row out, so a migration does not have to defend against it.
/// </remarks>
public sealed class CsvRow
{
    private readonly CsvDocument _document;
    private readonly List<CsvField> _fields;

    internal CsvRow(CsvDocument document, List<CsvField> fields)
    {
        _document = document;
        _fields = fields;
    }

    internal IReadOnlyList<CsvField> Fields => _fields;

    /// <summary>How many fields this row actually has.</summary>
    public int FieldCount => _fields.Count;

    /// <summary>Gets or sets a field by column name.</summary>
    /// <param name="column">The column name.</param>
    public string this[string column]
    {
        get
        {
            _document.RequireHeader();

            int index = _document.IndexOf(column);
            return index < 0 ? string.Empty : GetField(index);
        }

        set
        {
            _document.RequireHeader();

            int index = _document.IndexOf(column);
            if (index < 0)
                throw new MigrationException($"The CSV document has no column called '{column}'. Add it with AddColumn first.");

            SetField(index, value);
        }
    }

    /// <summary>Gets or sets a field by position.</summary>
    /// <param name="index">The zero-based field index.</param>
    public string this[int index]
    {
        get => GetField(index);
        set => SetField(index, value);
    }

    /// <summary>The row's values, in order.</summary>
    public IReadOnlyList<string> Values => _fields.Select(entry => entry.Value).ToList();

    /// <summary>Whether the row has no fields, or only empty ones.</summary>
    public bool IsEmpty => _fields.All(entry => entry.Value.Length == 0);

    internal string GetField(int index) =>
        index >= 0 && index < _fields.Count ? _fields[index].Value : string.Empty;

    internal void SetField(int index, string value)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

        while (_fields.Count <= index)
            _fields.Add(new CsvField(string.Empty, wasQuoted: false));

        _fields[index] = new CsvField(value ?? string.Empty, _fields[index].WasQuoted);
    }

    internal void InsertField(int index, string value)
    {
        while (_fields.Count < index)
            _fields.Add(new CsvField(string.Empty, wasQuoted: false));

        _fields.Insert(index, new CsvField(value ?? string.Empty, wasQuoted: false));
    }

    internal void RemoveField(int index)
    {
        if (index >= 0 && index < _fields.Count)
            _fields.RemoveAt(index);
    }

    internal void MoveField(int from, int to)
    {
        if (from < 0 || from >= _fields.Count)
            return;

        CsvField moved = _fields[from];
        _fields.RemoveAt(from);
        _fields.Insert(to > _fields.Count ? _fields.Count : to, moved);
    }

    /// <inheritdoc />
    public override string ToString() => string.Join(", ", Values);
}
