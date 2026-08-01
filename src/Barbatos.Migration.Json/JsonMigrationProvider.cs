// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Barbatos.Migration.Json;

/// <summary>
/// Transforms a JSON file - a settings file, a project document, a cached manifest - by
/// handing its DOM to a delegate.
/// </summary>
/// <remarks>
/// <para>
/// Working on the <see cref="JsonNode"/> DOM rather than on deserialised objects is what makes
/// this usable for migration at all. The old shape of the file no longer has a C# type in the
/// application - that type is exactly what the new version deleted - so there is nothing to
/// deserialise into. The DOM lets a step read the old shape, write the new one, and leave
/// untouched every property it does not know about, which matters because a user's settings
/// file may contain keys written by a plugin the migration has never heard of.
/// </para>
/// <para>
/// The write is atomic: the new document goes to a temporary file, is flushed to disk, and is
/// then renamed over the original. A settings file truncated by a power cut is a bricked
/// application, and this is the cheapest possible insurance against it.
/// </para>
/// </remarks>
public class JsonMigrationProvider : IMigrationProvider
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _relativePath;
    private readonly Action<JsonObject> _up;
    private readonly Action<JsonObject>? _down;

    /// <summary>Creates the provider.</summary>
    /// <param name="relativePath">The file, relative to <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="up">Transforms the document forwards. Mutate the object in place.</param>
    /// <param name="down">Transforms it backwards, or <see langword="null"/> for forward-only.</param>
    /// <param name="createIfMissing">
    /// Whether a missing file is created as <c>{}</c> and then transformed. Defaults to
    /// <see langword="true"/>, which is what makes a step that adds a settings file work on a
    /// fresh install as well as on an upgrade.
    /// </param>
    /// <param name="writeIndented">Whether to pretty-print. Defaults to <see langword="true"/>, since these files are usually meant to be human-readable.</param>
    public JsonMigrationProvider(
        string relativePath,
        Action<JsonObject> up,
        Action<JsonObject>? down = null,
        bool createIfMissing = true,
        bool writeIndented = true)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A relative path is required.", nameof(relativePath));

        _relativePath = relativePath;
        _up = up ?? throw new ArgumentNullException(nameof(up));
        _down = down;
        CreateIfMissing = createIfMissing;
        WriteIndented = writeIndented;
    }

    /// <inheritdoc />
    public virtual string Name => $"JSON ({_relativePath})";

    /// <inheritdoc />
    public virtual double Weight => 1.0;

    /// <inheritdoc />
    public bool CanDown => _down != null;

    /// <summary>Whether a missing file is created before the transform runs.</summary>
    public bool CreateIfMissing { get; }

    /// <summary>Whether the output is pretty-printed.</summary>
    public bool WriteIndented { get; }

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
        Action<JsonObject> transform,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string path = context.GetWorkingPath(_relativePath);
        progress?.Report(new MigrationProgress(0, $"Reading {_relativePath}"));

        TextFileContent? existing = AtomicFile.Read(path);

        JsonObject document;
        if (existing != null)
        {
            document = Parse(existing.Text, path);
        }
        else if (CreateIfMissing)
        {
            context.Logger.Log(MigrationLogLevel.Debug, $"'{path}' does not exist; creating it.");
            document = new JsonObject(NodeOptions);
        }
        else
        {
            context.Logger.Log(MigrationLogLevel.Debug, $"'{path}' does not exist; skipping.");
            progress?.Report(new MigrationProgress(100, $"{_relativePath} not present"));
            return Task.CompletedTask;
        }

        progress?.Report(new MigrationProgress(40, $"Transforming {_relativePath}"));
        transform(document);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MigrationProgress(70, $"Writing {_relativePath}"));

        // The original encoding is carried over, so a file another tool wrote with a BOM or as
        // UTF-16 does not silently change format just because a migration touched one key.
        AtomicFile.Write(path, document.ToJsonString(SerializerOptions()), existing?.Encoding);

        progress?.Report(new MigrationProgress(100, $"{_relativePath} updated"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// How the document is written back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoder matters more than it looks. <see cref="System.Text.Json"/> escapes every
    /// non-ASCII character by default, which is right for embedding JSON in a web page and wrong
    /// for a settings file on disk: a migration that renamed one key would turn every line of
    /// <c>"Xin chào {0}"</c> into <c>"Xin chào {0}"</c>. Still valid JSON, still the same
    /// value - and a file the user can no longer read or hand-edit, changed by an update they
    /// did not ask for it. The relaxed encoder leaves the text as the user wrote it.
    /// </para>
    /// <para>
    /// Both variants are created once and shared. A <see cref="JsonSerializerOptions"/> carries
    /// the serializer's type-metadata cache, so a fresh instance per write is not just an
    /// allocation - it throws that cache away and makes the run rebuild it every time.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc cref="IndentedOptions" />
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private JsonSerializerOptions SerializerOptions() => WriteIndented ? IndentedOptions : CompactOptions;

    private static JsonObject Parse(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject(NodeOptions);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, NodeOptions, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new MigrationException(
                $"'{path}' is not valid JSON, so it cannot be migrated. " +
                "Fix or delete the file and start the application again.", ex);
        }

        return node as JsonObject
            ?? throw new MigrationException($"'{path}' does not contain a JSON object at its root, so it cannot be migrated as one.");
    }

}
