using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
namespace Codexish.Server;

// The SDK's typed result deserializer rejects a whole call for one unfamiliar block. Keep the raw
// protocol result until each block has been decoded independently, preserving future extensions as text.
public static class McpResultDecoder
{
    public static bool IsTransportLoss(Exception error, bool sessionCompleted = false) => sessionCompleted || error switch
    {
        HttpRequestException http => http.StatusCode is null or System.Net.HttpStatusCode.NotFound,
        IOException or ObjectDisposedException or OperationCanceledException => true,
        _ => false
    };
    public static CallToolResult Call(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("The backend tools/call result has no content array.");
        bool? isError = null;
        if (raw.TryGetProperty("isError", out var error))
        {
            if (error.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) throw new JsonException("The backend isError value is not boolean.");
            if (error.ValueKind != JsonValueKind.Null) isError = error.GetBoolean();
        }
        return new CallToolResult { IsError = isError, Content = content.EnumerateArray().Select(DecodeBlock).ToList(),
            StructuredContent = raw.TryGetProperty("structuredContent", out var structured) ? structured.Clone() : null };
    }
    public static ContentBlock DecodeBlock(JsonElement raw)
    {
        try { return raw.Deserialize<ContentBlock>(McpJsonUtilities.DefaultOptions) ?? Unreadable(raw); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { return Unreadable(raw); }
    }
    public static TextContentBlock Unreadable(JsonElement raw, string defaultType = "unknown")
    {
        string type = defaultType;
        try { if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String) type = kind.GetString() ?? defaultType; }
        catch (InvalidOperationException) { }
        string text;
        try { text = Clean(raw, preserveBinary: false).GetRawText(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { text = Redaction.Apply(raw.GetRawText()).Text; }
        return new TextContentBlock { Text = $"[Unreadable MCP content block '{Redaction.Apply(type).Text}': {text}]" };
    }
    public static JsonElement Clean(JsonElement raw, bool preserveBinary = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, raw, preserveBinary);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value, bool preserveBinary)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                bool binary = value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "image" or "audio";
                writer.WriteStartObject(); foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (preserveBinary && (property.Name == "blob" || binary && property.Name == "data")) property.Value.WriteTo(writer);
                    else Write(writer, property.Value, preserveBinary);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item, preserveBinary); writer.WriteEndArray(); break;
            case JsonValueKind.String:
                try { writer.WriteStringValue(Redaction.Apply(value.GetString()!).Text); }
                catch (InvalidOperationException) { writer.WriteStringValue(Redaction.Apply(value.GetRawText()).Text); } break;
            default: value.WriteTo(writer); break;
        }
    }
}
