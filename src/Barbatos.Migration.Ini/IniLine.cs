// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration.Ini;

/// <summary>What a line in an INI file turned out to be.</summary>
internal enum IniLineKind
{
    /// <summary>Empty or whitespace.</summary>
    Blank,

    /// <summary>Starts with <c>;</c> or <c>#</c>.</summary>
    Comment,

    /// <summary><c>[section]</c>.</summary>
    Section,

    /// <summary><c>key = value</c>.</summary>
    Property,

    /// <summary>None of the above. Written back untouched.</summary>
    Unknown,
}

/// <summary>
/// One line of an INI file, split just far enough to edit the parts a migration cares about
/// while writing everything else back exactly as it arrived.
/// </summary>
/// <remarks>
/// A parsed line keeps its own indentation, the spacing around its <c>=</c>, whether its value
/// was quoted, and any trailing comment. Changing the value rewrites only the value; the rest of
/// the line is reassembled from the pieces the parser kept. A line the document never touches is
/// returned from its original string, so it cannot be altered even in principle.
/// </remarks>
internal sealed class IniLine
{
    private string? _raw;

    /// <summary>
    /// Whether this line came from the file. A parsed property is re-assembled from the spans
    /// the parser kept, so it keeps its spacing and trailing comment; one the migration added
    /// is rendered with the document's own conventions instead.
    /// </summary>
    private bool _parsed;

    private string _leading = string.Empty;
    private string _keySpacing = string.Empty;
    private string _valueSpacing = string.Empty;
    private string _trailing = string.Empty;
    private bool _quoted;

    private IniLine(IniLineKind kind, string section)
    {
        Kind = kind;
        Section = section;
        Key = string.Empty;
        Value = string.Empty;
    }

    public IniLineKind Kind { get; }

    /// <summary>The section this line belongs to. Reassigned when a key or section is moved.</summary>
    public string Section { get; set; }

    public string Key { get; set; }

    public string Value { get; set; }

    /// <summary>A line kept verbatim: blank, comment, or something unrecognised.</summary>
    public static IniLine Passthrough(IniLineKind kind, string raw, string section) =>
        new(kind, section) { _raw = raw };

    /// <summary>A parsed <c>[section]</c> header.</summary>
    public static IniLine SectionHeader(string raw, string section) =>
        new(IniLineKind.Section, section) { _raw = raw, Key = section };

    /// <summary>A parsed <c>key = value</c> line, split into its editable and preserved parts.</summary>
    public static IniLine Property(string raw, string section)
    {
        IniLine line = new(IniLineKind.Property, section) { _parsed = true };

        int equals = raw.IndexOf('=');
        string trimmedStart = raw.TrimStart();
        line._leading = raw.Substring(0, raw.Length - trimmedStart.Length);

        string keyPart = raw.Substring(line._leading.Length, equals - line._leading.Length);
        line.Key = keyPart.TrimEnd();
        line._keySpacing = keyPart.Substring(line.Key.Length);

        string rest = raw.Substring(equals + 1);
        string restTrimmed = rest.TrimStart();
        line._valueSpacing = rest.Substring(0, rest.Length - restTrimmed.Length);

        SplitValue(restTrimmed, line);
        return line;
    }

    /// <summary>A key the migration is adding, rendered with the document's own conventions.</summary>
    public static IniLine NewProperty(string key, string value, string section) =>
        new(IniLineKind.Property, section) { Key = key, Value = value };

    /// <summary>A section header the migration is adding.</summary>
    public static IniLine NewSection(string section) =>
        new(IniLineKind.Section, section) { Key = section };

    /// <summary>Renames a parsed section header, keeping its indentation and trailing comment.</summary>
    public void SetSectionName(string name)
    {
        if (_raw == null)
        {
            Key = name;
            return;
        }

        int open = _raw.IndexOf('[');
        int close = _raw.IndexOf(']');

        if (open < 0 || close < open)
        {
            Key = name;
            _raw = null;
            return;
        }

        _raw = _raw.Substring(0, open + 1) + name + _raw.Substring(close);
        Key = name;
    }

    public string Render(string defaultSeparator)
    {
        switch (Kind)
        {
            case IniLineKind.Property:
                return _parsed
                    ? _leading + Key + _keySpacing + "=" + _valueSpacing + RenderValue() + _trailing
                    : Key + defaultSeparator + RenderValue();

            case IniLineKind.Section:
                return _raw ?? $"[{Key}]";

            default:
                return _raw ?? string.Empty;
        }
    }

    private string RenderValue() =>
        _quoted || NeedsQuoting(Value) ? "\"" + Value.Replace("\"", "\\\"") + "\"" : Value;

    /// <summary>
    /// Whether writing the value bare would change what reading it back produces.
    /// </summary>
    /// <remarks>
    /// Deliberately the exact inverse of <see cref="IndexOfComment"/>: quoting anything merely
    /// <em>containing</em> a <c>;</c> would put quotes around a connection string that never
    /// needed them, and a line the migration was not asked to touch must come back byte for
    /// byte. Leading or trailing whitespace is the other case - it would be trimmed away.
    /// </remarks>
    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
            return false;

        return IndexOfComment(value) >= 0
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[value.Length - 1]);
    }

    private static void SplitValue(string text, IniLine line)
    {
        if (text.Length > 0 && text[0] == '"')
        {
            int closing = FindClosingQuote(text);
            if (closing > 0)
            {
                line._quoted = true;
                line.Value = text.Substring(1, closing - 1).Replace("\\\"", "\"");
                line._trailing = text.Substring(closing + 1);
                return;
            }
        }

        int comment = IndexOfComment(text);
        if (comment >= 0)
        {
            string beforeComment = text.Substring(0, comment);
            line.Value = beforeComment.TrimEnd();

            // The whitespace between the value and the comment belongs to the comment, so that
            // re-rendering a shorter value does not shuffle the comment leftwards.
            line._trailing = beforeComment.Substring(line.Value.Length) + text.Substring(comment);
            return;
        }

        line.Value = text.TrimEnd();
        line._trailing = text.Substring(line.Value.Length);
    }

    private static int FindClosingQuote(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }

            if (text[i] == '"')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Finds where an inline comment starts, or <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// A <c>;</c> or <c>#</c> only starts a comment when whitespace separates it from the value,
    /// or when it is the whole value. Treating every one as a comment truncates
    /// <c>ConnectionString=Server=localhost;Db=app</c> to <c>Server=localhost</c> - and a
    /// migration that then writes the file back has destroyed the rest of it. Requiring the
    /// separator is the convention every INI reader that supports inline comments at all
    /// follows, and it is the only reading that is safe to round-trip.
    /// </remarks>
    private static int IndexOfComment(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not (';' or '#'))
                continue;

            if (i == 0 || char.IsWhiteSpace(text[i - 1]))
                return i;
        }

        return -1;
    }

    public override string ToString()
    {
        StringBuilder builder = new();
        builder.Append(Kind).Append(": ").Append(Render(" = "));
        return builder.ToString();
    }
}
