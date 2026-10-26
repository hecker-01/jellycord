using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Jellycord.Discord;

/// <summary>
/// Shape-tolerant reads of Discord responses. The JsonNode indexer and GetValue throw when a
/// body isn't the expected object or a field has another type; these return null instead.
/// </summary>
internal static class JsonNodeExtensions
{
    public static string? Str(this JsonNode? node, string name)
        => Field(node, name) is { } v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Num(this JsonNode? node, string name)
        => Field(node, name) is { } v && v.TryGetValue<double>(out var d) ? d : null;

    private static JsonValue? Field(JsonNode? node, string name)
        => node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value as JsonValue : null;
}
