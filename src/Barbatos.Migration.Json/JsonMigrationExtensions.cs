// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text.Json.Nodes;

namespace Barbatos.Migration.Json;

/// <summary>
/// The handful of edits that make up almost every real JSON migration: rename a key, move one
/// into a section, drop one, add one with a default.
/// </summary>
/// <remarks>
/// Each of these is a no-op when the key is not there. That is deliberate and it is what makes
/// a migration safe to re-run: a step that half-applied before a crash, then re-ran after the
/// snapshot was restored, must not fail the second time because the key it renames is already
/// renamed.
/// </remarks>
public static class JsonMigrationExtensions
{
    /// <summary>Renames a property, preserving its value and skipping if it is absent.</summary>
    public static JsonObject RenameProperty(this JsonObject json, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.TryGetPropertyValue(from, out JsonNode? value))
            return json;

        json.Remove(from);
        json[to] = value?.DeepClone();
        return json;
    }

    /// <summary>Removes a property if present.</summary>
    public static JsonObject RemoveProperty(this JsonObject json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);

        json.Remove(name);
        return json;
    }

    /// <summary>Sets a property only when it is missing, so a user's own value is never overwritten.</summary>
    public static JsonObject SetDefault(this JsonObject json, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (!json.ContainsKey(name))
            json[name] = value;

        return json;
    }

    /// <summary>Sets a property, replacing any existing value.</summary>
    public static JsonObject Set(this JsonObject json, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(json);

        json[name] = value;
        return json;
    }

    /// <summary>
    /// Moves a top-level property into a nested object, creating the section if needed. The
    /// usual shape of "we grew enough settings that they need grouping".
    /// </summary>
    public static JsonObject MoveIntoSection(this JsonObject json, string propertyName, string sectionName, string? newName = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.TryGetPropertyValue(propertyName, out JsonNode? value))
            return json;

        JsonObject section = json[sectionName] as JsonObject ?? [];
        json[sectionName] = section;

        json.Remove(propertyName);
        section[newName ?? propertyName] = value?.DeepClone();
        return json;
    }

    /// <summary>Lifts a property out of a nested object back to the top level - the inverse of <see cref="MoveIntoSection"/>.</summary>
    public static JsonObject MoveOutOfSection(this JsonObject json, string sectionName, string propertyName, string? newName = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json[sectionName] is not JsonObject section || !section.TryGetPropertyValue(propertyName, out JsonNode? value))
            return json;

        section.Remove(propertyName);
        json[newName ?? propertyName] = value?.DeepClone();

        if (section.Count == 0)
            json.Remove(sectionName);

        return json;
    }

    /// <summary>
    /// Rewrites a property's value through <paramref name="convert"/> - the escape hatch for
    /// changes of type, such as minutes stored as a number becoming an ISO-8601 duration string.
    /// </summary>
    public static JsonObject ConvertProperty(this JsonObject json, string name, Func<JsonNode?, JsonNode?> convert)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(convert);
        if (!json.TryGetPropertyValue(name, out JsonNode? value))
            return json;

        json[name] = convert(value?.DeepClone());
        return json;
    }

    /// <summary>
    /// Applies <paramref name="update"/> to every object in an array property - the shape a
    /// "each saved entry gains a field" migration takes.
    /// </summary>
    /// <remarks>
    /// A no-op when the property is missing, is not an array, or holds entries that are not
    /// objects. Real documents contain all three - a list a newer build wrote as a bare string,
    /// a null left behind by a crash, a key the user deleted by hand - and a migration that
    /// throws on any of them is a migration that cannot be re-run after a restored snapshot.
    /// </remarks>
    /// <param name="json">The document.</param>
    /// <param name="name">The array property.</param>
    /// <param name="update">Applied to each object entry, in order.</param>
    /// <example>
    /// <code>
    /// json.ForEachInArray("recentFiles", entry => entry
    ///     .RenameProperty("path", "fullPath")
    ///     .SetDefault("openedAt", DateTimeOffset.UtcNow.ToString("O")));
    /// </code>
    /// </example>
    public static JsonObject ForEachInArray(this JsonObject json, string name, Action<JsonObject> update)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(update);

        if (json[name] is not JsonArray array)
            return json;

        foreach (JsonNode? entry in array)
        {
            if (entry is JsonObject item)
                update(item);
        }

        return json;
    }

    /// <summary>
    /// Climbs back to the top of the document, so a chain that stepped down into a nested object
    /// can carry on at the root.
    /// </summary>
    /// <remarks>
    /// <see cref="Section"/> returns the nested object, which is what makes
    /// <c>json.Section("editor").Section("font").Set("size", 16)</c> read well - and also means
    /// everything after it applies to <c>font</c>. <c>Root()</c> is the way back up. Plain
    /// statements are just as good; this only exists so a single expression does not have to be
    /// broken up when it would otherwise read better whole.
    /// </remarks>
    /// <exception cref="MigrationException">The document's root is not an object.</exception>
    public static JsonObject Root(this JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        JsonNode current = node;
        while (current.Parent is JsonNode parent)
            current = parent;

        return current as JsonObject
            ?? throw new MigrationException("The root of this JSON document is not an object, so there is nothing to return to.");
    }

    /// <summary>Gets a nested object, creating it if it is missing.</summary>
    public static JsonObject Section(this JsonObject json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (json[name] is JsonObject existing)
            return existing;

        JsonObject created = [];
        json[name] = created;
        return created;
    }
}
