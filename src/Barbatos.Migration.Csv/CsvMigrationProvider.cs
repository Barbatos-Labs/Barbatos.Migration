// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration.Csv;

/// <summary>
/// Reshapes a delimited data file by handing its table to a delegate.
/// </summary>
/// <remarks>
/// <para>
/// The work is the same shape as migrating a database table - add a column, split one into two,
/// drop one nobody reads any more - on a file the user may well also open in Excel. The
/// delimiter, quoting style, line endings and header are all preserved, so only the cells the
/// migration touches come back different.
/// </para>
/// <para>
/// The write is atomic and keeps the original encoding.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// new CsvMigrationProvider(
///     "licences.csv",
///     up:   csv => csv.SplitColumn("FullName", name => name.Split(' ', 2), "FirstName", "LastName")
///                     .AddColumn("Archived", "false"),
///     down: csv => csv.MergeColumns("FullName", parts => string.Join(" ", parts), "FirstName", "LastName")
///                     .RemoveColumn("Archived"));
/// </code>
/// For a file large enough that the migration needs to stay cancellable, take the progress and
/// token as well:
/// <code>
/// new CsvMigrationProvider(
///     "history.csv",
///     up: (csv, progress, ct) => csv.UpdateRows(
///         row => row["Timestamp"] = DateTimeOffset.Parse(row["Timestamp"]).ToString("O"),
///         progress,
///         ct));
/// </code>
/// </example>
public class CsvMigrationProvider : IMigrationProvider
{
    private readonly string _relativePath;
    private readonly Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken> _up;
    private readonly Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken>? _down;

    /// <summary>Creates the provider.</summary>
    /// <param name="relativePath">The file, relative to <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="up">Transforms the table forwards. Mutate it in place.</param>
    /// <param name="down">Transforms it backwards, or <see langword="null"/> for forward-only.</param>
    /// <param name="hasHeader">Whether the first row is a header. Defaults to <see langword="true"/>.</param>
    /// <param name="delimiter">The field separator, or <see langword="null"/> to detect it.</param>
    public CsvMigrationProvider(
        string relativePath,
        Action<CsvDocument> up,
        Action<CsvDocument>? down = null,
        bool hasHeader = true,
        char? delimiter = null)
        : this(
            relativePath,
            (document, _, _) => up(document),
            down == null ? null : (document, _, _) => down(document),
            hasHeader,
            delimiter)
    {
        ArgumentNullException.ThrowIfNull(up);
    }

    /// <summary>
    /// Creates the provider with delegates that also receive the progress reporter and the
    /// cancellation token - for files big enough that a migration over them has to stay
    /// responsive and interruptible.
    /// </summary>
    /// <param name="relativePath">The file, relative to <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="up">Transforms the table forwards.</param>
    /// <param name="down">Transforms it backwards, or <see langword="null"/> for forward-only.</param>
    /// <param name="hasHeader">Whether the first row is a header.</param>
    /// <param name="delimiter">The field separator, or <see langword="null"/> to detect it.</param>
    public CsvMigrationProvider(
        string relativePath,
        Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken> up,
        Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken>? down = null,
        bool hasHeader = true,
        char? delimiter = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A relative path is required.", nameof(relativePath));

        _relativePath = relativePath;
        _up = up ?? throw new ArgumentNullException(nameof(up));
        _down = down;
        HasHeader = hasHeader;
        Delimiter = delimiter;
    }

    /// <inheritdoc />
    public virtual string Name => $"CSV ({_relativePath})";

    /// <summary>Defaults to <c>2.0</c>: a data file is usually more work than a settings file, and less than a schema change.</summary>
    public virtual double Weight { get; set; } = 2.0;

    /// <inheritdoc />
    public bool CanDown => _down != null;

    /// <summary>Whether the first row is treated as a header.</summary>
    public bool HasHeader { get; }

    /// <summary>The field separator, or <see langword="null"/> to detect it per file.</summary>
    public char? Delimiter { get; }

    /// <summary>
    /// Whether a missing file is created from the columns the transform adds. Defaults to
    /// <see langword="false"/> - unlike a settings file, an absent data file usually means
    /// "this user has no records yet", and inventing one would be presumptuous.
    /// </summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>How values are quoted on the way out. Defaults to preserving the original style.</summary>
    public CsvQuoteStyle QuoteStyle { get; set; } = CsvQuoteStyle.PreserveOriginal;

    /// <inheritdoc />
    public Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        TransformAsync(context, _up, progress, cancellationToken);

    /// <inheritdoc />
    public Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        _down != null
            ? TransformAsync(context, _down, progress, cancellationToken)
            : throw new NotSupportedException($"'{Name}' is forward-only; it does not implement a downgrade.");

    private Task TransformAsync(
        IMigrationContext context,
        Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken> transform,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string path = context.GetWorkingPath(_relativePath);
        progress?.Report(new MigrationProgress(0, $"Reading {_relativePath}"));

        TextFileContent? existing = AtomicFile.Read(path);

        if (existing == null && !CreateIfMissing)
        {
            context.Logger.Log(MigrationLogLevel.Debug, $"'{path}' does not exist; skipping.");
            progress?.Report(new MigrationProgress(100, $"{_relativePath} not present"));
            return Task.CompletedTask;
        }

        CsvDocument document;
        try
        {
            document = existing == null
                ? CsvDocument.Create([], Delimiter ?? ',')
                : CsvDocument.Parse(existing.Text, HasHeader, Delimiter);
        }
        catch (MigrationException ex)
        {
            throw new MigrationException($"'{path}' cannot be migrated. {ex.Message}", ex);
        }

        // Create() always starts a document with a header, which is right for the common case
        // but not for a provider that was told this file has none.
        document.HasHeader = HasHeader;

        document.QuoteStyle = QuoteStyle;

        progress?.Report(new MigrationProgress(5, $"Transforming {_relativePath}"));
        transform(document, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MigrationProgress(90, $"Writing {_relativePath}"));

        AtomicFile.Write(path, document.ToCsvString(), existing?.Encoding);

        progress?.Report(new MigrationProgress(100, $"{_relativePath} updated"));
        return Task.CompletedTask;
    }
}
