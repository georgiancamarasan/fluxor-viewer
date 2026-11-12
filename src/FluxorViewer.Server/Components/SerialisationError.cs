using System.Text;
using System.Text.Json;

namespace FluxorViewer.Server.Components;

/// <summary>States the middleware couldn't serialise, which it sends as exactly <c>{"$error": "message"}</c>.</summary>
public static class SerialisationError
{
    /// <summary>
    /// The message when <paramref name="json"/> is exactly <c>{"$error": "message"}</c>; null for anything else,
    /// including a state that merely has a <c>$error</c> property. Stops reading at the first token that doesn't fit,
    /// so large states cost almost nothing.
    /// </summary>
    public static string? In(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("$error"))
                return null;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                return null;
            var message = reader.GetString();
            return reader.Read() && reader.TokenType == JsonTokenType.EndObject ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
