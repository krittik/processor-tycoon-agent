using System.Text.Json;
using System.Text.Json.Nodes;

// Type-safe access to bridge replies. A reply can be partial while the game shuts down (dropped connection, empty body,
// a non-object body, or a string where an object was expected); these helpers never throw on unexpected node types.
internal static class JsonSafe
{
    // Parses a bridge response body; anything but a JSON object is a JsonException (handled like a lost response).
    internal static JsonObject ParseObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new JsonException("Empty server response.");
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw new JsonException("Malformed server response: " + error.Message); }
        return node as JsonObject ?? throw new JsonException("Server response is not a JSON object.");
    }

    // node.At("operation", "result", "data"): null as soon as a step is missing or not an object.
    internal static JsonNode? At(this JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out node)) return null;
        }
        return node;
    }

    internal static bool? Bool(this JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : node is JsonValue s && s.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed : null;
    internal static string? Str(this JsonNode? node) => node switch { null => null, JsonValue v when v.TryGetValue<string>(out var s) => s, JsonValue v => v.ToJsonString(), _ => null };
    internal static int? Int(this JsonNode? node) => node is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue ? (int)l : v.TryGetValue<double>(out var d) ? (int)d : null : null;
}
