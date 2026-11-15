using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxorViewer.Middleware;

/// <summary>
/// Writes a long string a segment at a time, so the writer never asks for room for the whole string escaped at worst
/// (several times its length) in one piece. The JSON is the same as writing it whole.
/// </summary>
internal sealed class SegmentedStringConverter : JsonConverter<string>
{
    /// <summary>The most characters written at once; shorter strings are written whole.</summary>
    internal const int SegmentLength = 4096;

    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        var rest = value.AsSpan();
        while (rest.Length > SegmentLength)
        {
            writer.WriteStringValueSegment(rest[..SegmentLength], isFinalSegment: false);
            rest = rest[SegmentLength..];
        }
        if (rest.Length == value.Length)
            writer.WriteStringValue(value);
        else
            writer.WriteStringValueSegment(rest, isFinalSegment: true);
    }

    /// <inheritdoc />
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString()!;

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value);
}
