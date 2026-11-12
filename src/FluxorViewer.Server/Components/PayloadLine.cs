using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FluxorViewer.Server.Components;

/// <summary>
/// An Action's Payload as the payload strip shows it collapsed: one line of compact JSON with strings decoded
/// (<c>"café"</c>, not <c>"café"</c>) and numbers as received, cut to <see cref="MaxLength"/> characters.
/// </summary>
/// <param name="Text">The line to show.</param>
/// <param name="CanExpand">Whether there's a tree to open: a non-empty object or array. False for malformed JSON.</param>
public sealed record PayloadLine(string Text, bool CanExpand)
{
    /// <summary>Longer lines are cut here and end in <c>…</c>, so a large Payload doesn't cross the circuit whole.</summary>
    public const int MaxLength = 500;

    private static readonly JsonWriterOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static PayloadLine Of(string json)
    {
        try
        {
            using var document = ReceivedJson.Parse(json);
            var root = document.RootElement;
            var canExpand = root.ValueKind switch
            {
                JsonValueKind.Object => root.EnumerateObject().Any(),
                JsonValueKind.Array => root.GetArrayLength() > 0,
                _ => false,
            };

            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, Readable))
                root.WriteTo(writer);
            return new PayloadLine(Cut(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)), canExpand);
        }
        catch (JsonException)
        {
            return new PayloadLine(Cut(json), false);
        }
    }

    /// <summary>Cuts to <see cref="MaxLength"/>, never between the halves of a surrogate pair.</summary>
    private static string Cut(string text)
    {
        if (text.Length <= MaxLength)
            return text;
        var end = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return string.Concat(text.AsSpan(0, end), "…");
    }
}
