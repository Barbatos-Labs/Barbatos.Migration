// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration.Ini;

/// <summary>
/// Reshapes an INI file - a settings file, a legacy configuration, a profile - by handing its
/// document to a delegate.
/// </summary>
/// <remarks>
/// <para>
/// INI is the format applications that predate this framework are most likely to have their
/// settings in, which makes it the format a first migration most often has to deal with. The
/// document model is deliberately format-preserving: comments, blank lines, key order and
/// spacing all survive, and only what the delegate touches changes. See
/// <see cref="IniDocument"/> for why that matters more here than it looks.
/// </para>
/// <para>
/// The write is atomic - temporary file, flushed, renamed over the original - and keeps the
/// file's original encoding, so a settings file another tool wrote with a BOM does not change
/// format because a migration renamed one key.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// new IniMigrationProvider(
///     "settings.ini",
///     up:   ini => ini.MoveKey("General", "fontSize", "Editor")
///                     .RenameKey("Editor", "fontSize", "fontSizePx")
///                     .SetDefault("General", "language", "vi"),
///     down: ini => ini.RemoveKey("General", "language")
///                     .RenameKey("Editor", "fontSizePx", "fontSize")
///                     .MoveKey("Editor", "fontSize", "General"));
/// </code>
/// </example>
public class IniMigrationProvider : IMigrationProvider
{
    private readonly string _relativePath;
    private readonly Action<IniDocument> _up;
    private readonly Action<IniDocument>? _down;

    /// <summary>Creates the provider.</summary>
    /// <param name="relativePath">The file, relative to <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="up">Transforms the document forwards. Mutate it in place.</param>
    /// <param name="down">Transforms it backwards, or <see langword="null"/> for forward-only.</param>
    /// <param name="createIfMissing">
    /// Whether a missing file is created empty and then transformed. Defaults to
    /// <see langword="true"/>, which is what makes a step that adds a settings file work on a
    /// fresh install as well as on an upgrade.
    /// </param>
    /// <param name="caseSensitive">
    /// Whether section and key names are matched case-sensitively. Defaults to
    /// <see langword="false"/>, matching almost every INI consumer.
    /// </param>
    public IniMigrationProvider(
        string relativePath,
        Action<IniDocument> up,
        Action<IniDocument>? down = null,
        bool createIfMissing = true,
        bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A relative path is required.", nameof(relativePath));

        _relativePath = relativePath;
        _up = up ?? throw new ArgumentNullException(nameof(up));
        _down = down;
        CreateIfMissing = createIfMissing;
        CaseSensitive = caseSensitive;
    }

    /// <inheritdoc />
    public virtual string Name => $"INI ({_relativePath})";

    /// <inheritdoc />
    public virtual double Weight => 1.0;

    /// <inheritdoc />
    public bool CanDown => _down != null;

    /// <summary>Whether a missing file is created before the transform runs.</summary>
    public bool CreateIfMissing { get; }

    /// <summary>Whether section and key names are matched case-sensitively.</summary>
    public bool CaseSensitive { get; }

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
        Action<IniDocument> transform,
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

        if (existing == null)
            context.Logger.Log(MigrationLogLevel.Debug, $"'{path}' does not exist; creating it.");

        IniDocument document;
        try
        {
            document = IniDocument.Parse(existing?.Text ?? string.Empty, CaseSensitive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MigrationException($"'{path}' could not be read as an INI file, so it cannot be migrated.", ex);
        }

        progress?.Report(new MigrationProgress(40, $"Transforming {_relativePath}"));
        transform(document);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MigrationProgress(70, $"Writing {_relativePath}"));

        AtomicFile.Write(path, document.ToIniString(), existing?.Encoding);

        progress?.Report(new MigrationProgress(100, $"{_relativePath} updated"));
        return Task.CompletedTask;
    }
}
