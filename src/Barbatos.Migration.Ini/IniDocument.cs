// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration.Ini;

/// <summary>
/// An INI file as an editable, <b>format-preserving</b> document.
/// </summary>
/// <remarks>
/// <para>
/// The obvious way to migrate an INI file is to parse it into a dictionary, edit that, and
/// write the dictionary back out. It is also wrong for this purpose: the file that comes back
/// has lost every comment the user wrote, every blank line that grouped related settings, the
/// original key order, and whatever spacing convention the file had. A user who opens their
/// carefully annotated <c>config.ini</c> after an update and finds it reduced to a flat
/// alphabetical list has, from their point of view, had their file damaged by the update.
/// </para>
/// <para>
/// So this keeps the file as the list of lines it actually is. Each line is classified once -
/// blank, comment, section header, or key/value - and anything the document is not asked to
/// change is written back <em>byte for byte</em>. Editing a value rewrites only the value part
/// of that one line, keeping its indentation, its spacing around the <c>=</c>, and any trailing
/// comment.
/// </para>
/// <para>
/// It is the same principle behind the JSON provider preserving properties it does not
/// recognise, applied to a format that has no DOM to lean on.
/// </para>
/// </remarks>
public sealed class IniDocument
{
    private readonly List<IniLine> _lines = [];
    private readonly StringComparer _comparer;

    private IniDocument(StringComparer comparer, string newLine)
    {
        _comparer = comparer;
        NewLine = newLine;
    }

    /// <summary>
    /// The line ending the file uses. Detected when parsing, so a file written on Windows stays
    /// CRLF and one written on Linux stays LF - otherwise the first migration would show up as
    /// a whole-file change in the user's version control.
    /// </summary>
    public string NewLine { get; set; }

    /// <summary>
    /// Whether the file ends with a trailing newline. Preserved from the original.
    /// </summary>
    public bool EndsWithNewLine { get; set; } = true;

    /// <summary>
    /// The character written between key and value when a <em>new</em> key is added. Existing
    /// lines keep whatever they already had.
    /// </summary>
    public string KeyValueSeparator { get; set; } = " = ";

    /// <summary>The character used when a new comment is written.</summary>
    public char CommentPrefix { get; set; } = ';';

    /// <summary>The names of every section, in file order. The unnamed leading section is <see cref="string.Empty"/>.</summary>
    public IReadOnlyList<string> SectionNames =>
        _lines.Where(line => line.Kind == IniLineKind.Section)
            .Select(line => line.Section)
            .Distinct(_comparer)
            .ToList();

    /// <summary>Parses <paramref name="text"/>.</summary>
    /// <param name="text">The file contents.</param>
    /// <param name="caseSensitive">
    /// Whether section and key names are matched case-sensitively. Defaults to
    /// <see langword="false"/>, which is what almost every INI consumer does.
    /// </param>
    public static IniDocument Parse(string text, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringComparer comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        IniDocument document = new(comparer, DetectNewLine(text))
        {
            EndsWithNewLine = text.Length == 0 || text[text.Length - 1] is '\n' or '\r',
        };

        string currentSection = string.Empty;

        foreach (string raw in SplitLines(text))
        {
            string trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                document._lines.Add(IniLine.Passthrough(IniLineKind.Blank, raw, currentSection));
                continue;
            }

            if (trimmed[0] is ';' or '#')
            {
                document._lines.Add(IniLine.Passthrough(IniLineKind.Comment, raw, currentSection));
                continue;
            }

            if (trimmed[0] == '[')
            {
                int close = trimmed.IndexOf(']');
                if (close > 0)
                {
                    currentSection = trimmed.Substring(1, close - 1).Trim();
                    document._lines.Add(IniLine.SectionHeader(raw, currentSection));
                    continue;
                }
            }

            int separator = trimmed.IndexOf('=');
            if (separator > 0)
            {
                document._lines.Add(IniLine.Property(raw, currentSection));
                continue;
            }

            // Not blank, not a comment, not a section, not a key/value pair. Whatever it is,
            // it is not ours to interpret - keep it exactly as it is.
            document._lines.Add(IniLine.Passthrough(IniLineKind.Unknown, raw, currentSection));
        }

        return document;
    }

    /// <summary>Renders the document back to text.</summary>
    public string ToIniString()
    {
        StringBuilder builder = new();

        for (int i = 0; i < _lines.Count; i++)
        {
            builder.Append(_lines[i].Render(KeyValueSeparator));

            if (i < _lines.Count - 1 || EndsWithNewLine)
                builder.Append(NewLine);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => ToIniString();

    // ---------------------------------------------------------------- reading

    /// <summary>Whether <paramref name="key"/> exists in <paramref name="section"/>.</summary>
    /// <param name="section">The section name; <see cref="string.Empty"/> for the unnamed leading section.</param>
    /// <param name="key">The key name.</param>
    public bool ContainsKey(string section, string key) => Find(section, key) != null;

    /// <summary>Whether the document has a section called <paramref name="section"/>.</summary>
    public bool ContainsSection(string section) =>
        _lines.Any(line => line.Kind == IniLineKind.Section && _comparer.Equals(line.Section, section));

    /// <summary>Gets a value, or <see langword="null"/> when the key is absent.</summary>
    /// <param name="section">The section name; <see cref="string.Empty"/> for the unnamed leading section.</param>
    /// <param name="key">The key name.</param>
    public string? GetValue(string section, string key) => Find(section, key)?.Value;

    /// <summary>Gets a value, falling back to <paramref name="defaultValue"/> when the key is absent.</summary>
    public string GetValue(string section, string key, string defaultValue) =>
        Find(section, key)?.Value ?? defaultValue;

    /// <summary>The keys in <paramref name="section"/>, in file order.</summary>
    public IReadOnlyList<string> KeysIn(string section) =>
        _lines.Where(line => line.Kind == IniLineKind.Property && _comparer.Equals(line.Section, section))
            .Select(line => line.Key)
            .ToList();

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Sets a value, adding the key - and the section - when they are missing.
    /// </summary>
    /// <param name="section">The section name; <see cref="string.Empty"/> for the unnamed leading section.</param>
    /// <param name="key">The key name.</param>
    /// <param name="value">The value to store.</param>
    public IniDocument Set(string section, string key, string value)
    {
        Require(key, nameof(key));

        IniLine? existing = Find(section, key);
        if (existing != null)
        {
            existing.Value = value;
            return this;
        }

        Insert(section, IniLine.NewProperty(key, value, section));
        return this;
    }

    /// <summary>
    /// Sets a value only when the key is missing, so a value the user has already chosen is
    /// never overwritten.
    /// </summary>
    public IniDocument SetDefault(string section, string key, string value)
    {
        Require(key, nameof(key));

        if (Find(section, key) == null)
            Insert(section, IniLine.NewProperty(key, value, section));

        return this;
    }

    /// <summary>Renames a key, keeping its value, its position and its trailing comment.</summary>
    public IniDocument RenameKey(string section, string from, string to)
    {
        Require(from, nameof(from));
        Require(to, nameof(to));

        IniLine? line = Find(section, from);
        if (line == null)
            return this;

        line.Key = to;
        return this;
    }

    /// <summary>Removes a key if present.</summary>
    public IniDocument RemoveKey(string section, string key)
    {
        IniLine? line = Find(section, key);
        if (line != null)
            _lines.Remove(line);

        return this;
    }

    /// <summary>Rewrites a value through <paramref name="convert"/>, for changes of type or unit.</summary>
    /// <example>
    /// Seconds stored as a bare number becoming an ISO-8601 duration:
    /// <code>
    /// ini.ConvertValue("Session", "timeout", value => TimeSpan.FromSeconds(int.Parse(value)).ToString("c"));
    /// </code>
    /// </example>
    public IniDocument ConvertValue(string section, string key, Func<string, string> convert)
    {
        ArgumentNullException.ThrowIfNull(convert);

        IniLine? line = Find(section, key);
        if (line != null)
            line.Value = convert(line.Value);

        return this;
    }

    /// <summary>
    /// Moves a key to another section, creating it if needed. The usual shape of "these settings
    /// grew enough to need grouping".
    /// </summary>
    /// <param name="fromSection">The section the key is in now.</param>
    /// <param name="key">The key to move.</param>
    /// <param name="toSection">The section to move it to.</param>
    /// <param name="newName">A new name for the key, or <see langword="null"/> to keep it.</param>
    public IniDocument MoveKey(string fromSection, string key, string toSection, string? newName = null)
    {
        IniLine? line = Find(fromSection, key);
        if (line == null)
            return this;

        _lines.Remove(line);

        line.Key = newName ?? line.Key;
        line.Section = toSection;
        Insert(toSection, line);

        return this;
    }

    /// <summary>Renames a section, keeping every key in it and their order.</summary>
    public IniDocument RenameSection(string from, string to)
    {
        Require(to, nameof(to));

        bool found = false;

        foreach (IniLine line in _lines)
        {
            if (!_comparer.Equals(line.Section, from))
                continue;

            if (line.Kind == IniLineKind.Section)
            {
                line.SetSectionName(to);
                found = true;
            }

            line.Section = to;
        }

        // Renaming the unnamed leading section means giving it a header, which it does not have.
        if (!found && from.Length == 0)
            EnsureSection(to);

        return this;
    }

    /// <summary>Removes a section and every line that belongs to it.</summary>
    /// <remarks>
    /// <para>
    /// The parser assigns every line to the section header above it, which puts a comment that
    /// sits directly on top of the <em>next</em> header into the section being removed. Read by
    /// a human, that comment documents the section below it:
    /// </para>
    /// <code>
    /// [Messages]
    /// Greeting = ...
    ///
    /// ; Timeouts, in seconds
    /// [Advanced]
    /// </code>
    /// <para>
    /// So removing <c>[Messages]</c> keeps the trailing run of comment and blank lines and hands
    /// it to <c>[Advanced]</c>. Everything else - the header, the keys, and any comments among
    /// them - goes.
    /// </para>
    /// </remarks>
    public IniDocument RemoveSection(string section)
    {
        List<int> block = [];
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_comparer.Equals(_lines[i].Section, section))
                block.Add(i);
        }

        if (block.Count == 0)
            return this;

        string? nextSection = null;
        for (int i = block[block.Count - 1] + 1; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == IniLineKind.Section)
            {
                nextSection = _lines[i].Section;
                break;
            }
        }

        // Only worth keeping when there is a section below for it to describe, and only when the
        // run actually says something - a bare blank line is separation, not documentation, and
        // leaving it behind just puts a stray gap where the section used to be.
        int removeUpTo = block.Count;
        if (nextSection != null)
        {
            int runStart = block.Count;
            bool hasComment = false;

            while (runStart > 0 && _lines[block[runStart - 1]].Kind is IniLineKind.Comment or IniLineKind.Blank)
            {
                runStart--;
                hasComment |= _lines[block[runStart]].Kind == IniLineKind.Comment;
            }

            if (hasComment)
            {
                removeUpTo = runStart;
                for (int k = runStart; k < block.Count; k++)
                    _lines[block[k]].Section = nextSection;
            }
        }

        for (int k = removeUpTo - 1; k >= 0; k--)
            _lines.RemoveAt(block[k]);

        return this;
    }

    /// <summary>Adds a section header if the document does not have one already.</summary>
    public IniDocument EnsureSection(string section)
    {
        Require(section, nameof(section));

        if (!ContainsSection(section))
            _lines.Add(IniLine.NewSection(section));

        return this;
    }

    /// <summary>Appends a comment line to <paramref name="section"/>.</summary>
    public IniDocument AddComment(string section, string comment)
    {
        EnsureSection(section);
        Insert(section, IniLine.Passthrough(IniLineKind.Comment, $"{CommentPrefix} {comment}", section));
        return this;
    }

    // ---------------------------------------------------------------- internals

    private IniLine? Find(string section, string key) =>
        _lines.FirstOrDefault(line =>
            line.Kind == IniLineKind.Property
            && _comparer.Equals(line.Section, section)
            && _comparer.Equals(line.Key, key));

    private void Insert(string section, IniLine line)
    {
        line.Section = section;

        if (section.Length > 0)
            EnsureSection(section);

        // Placed after the last line that belongs to the section, so a new key lands with its
        // neighbours rather than at the bottom of the file.
        int insertAt = _lines.Count;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (!_comparer.Equals(_lines[i].Section, section))
                continue;

            // Trailing blank lines usually separate this section from the next one, so keep
            // them below the new key.
            insertAt = i + 1;
            while (insertAt > 0 && _lines[insertAt - 1].Kind == IniLineKind.Blank)
                insertAt--;

            break;
        }

        _lines.Insert(insertAt, line);
    }

    private static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A name is required.", parameterName);
    }

    private static string DetectNewLine(string text)
    {
        int index = text.IndexOf('\n');
        if (index < 0)
            return Environment.NewLine;

        return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (text.Length == 0)
            yield break;

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r')
                continue;

            yield return text.Substring(start, i - start);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;

            start = i + 1;
        }

        if (start < text.Length)
            yield return text.Substring(start);
    }
}
