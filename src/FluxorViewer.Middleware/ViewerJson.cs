using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Serialises one Payload or Feature state for the Viewer, sending a Serialisation error, exactly
/// <c>{"$error": "message"}</c>, in its place if it won't serialise or its JSON is over <c>MaxValueSize</c>. Each failing
/// Action type or Feature is logged at Warning once per process (a Blazor Server app has many Sessions), then at Debug.
/// </summary>
internal sealed class ViewerJson(FluxorViewerOptions settings, ILogger logger)
{
    /// <summary>Longer Serialisation error messages are cut to this and end in <c>…</c>.</summary>
    internal const int MaxErrorLength = 500;

    // Each problem (its log message) with each subject is logged at Warning once.
    private static readonly ConcurrentDictionary<(string Message, string Subject), bool> Warned = new();

    private readonly JsonSerializerOptions options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = settings.MaxDepth,
    };

    public string Payload(object action)
    {
        var type = action.GetType();
        return Serialise(action, type, $"Action {TypeNames.Readable(type)}");
    }

    public string FeatureState(string featureName, object? state, Type stateType) =>
        Serialise(state, stateType, $"Feature {featureName}");

    private string Serialise(object? value, Type type, string subject)
    {
        CappedBuffer buffer;
        try
        {
            buffer = Write(value, type);
        }
        catch (Exception ex)
        {
            Log(subject, ex, "FluxorViewer: could not serialise {Subject}; sent a Serialisation error instead.");
            return Error(Describe(ex));
        }

        if (!buffer.IsOverCap)
            return buffer.Text;
        Log(subject, null, "FluxorViewer: {Subject} is larger than MaxValueSize; sent a Serialisation error instead.");
        return Error($"Larger than MaxValueSize ({ByteSize.Readable(settings.MaxValueSize)}): {ByteSize.Readable(buffer.Length)}");
    }

    /// <summary>
    /// The Serialisation error sent in place of <paramref name="valueJson"/>, a value of <paramref name="subject"/> (e.g.
    /// <c>Feature Todos</c>) left out to keep its message under the Viewer's limit; logged once per subject at Warning.
    /// </summary>
    public string LeftOut(string subject, string valueJson)
    {
        Log(subject, null, "FluxorViewer: left out {Subject} to keep a message under the Viewer's limit; sent a Serialisation error instead.");
        return Error($"Left out to stay under the Viewer's {ByteSize.Readable(JsonLimits.MaxMessageSize)} limit " +
                     $"({ByteSize.Readable(Encoding.UTF8.GetByteCount(valueJson))})");
    }

    private CappedBuffer Write(object? value, Type type)
    {
        var buffer = new CappedBuffer(settings.MaxValueSize);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = options.MaxDepth });
        JsonSerializer.Serialize(writer, value, type, options);
        return buffer;
    }

    private void Log(string subject, Exception? exception, string message)
    {
        var level = Warned.TryAdd((message, subject), true) ? LogLevel.Warning : LogLevel.Debug;
        logger.Log(level, exception, message, subject);
    }

    private static string Error(string message)
    {
        if (message.Length > MaxErrorLength)
            message = message[..(MaxErrorLength - 1)] + "…";
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["$error"] = message });
    }

    private string Describe(Exception ex) =>
        // With IgnoreCycles on, this "possible object cycle" can only be depth, and its advice (ReferenceHandler.Preserve)
        // isn't the developer's to take. System.Text.Json's messages aren't localised, so matching the text is safe.
        ex is JsonException { Path: var path } && ex.Message.Contains($"maximum allowed depth of {settings.MaxDepth}")
            ? $"Nested deeper than MaxDepth ({settings.MaxDepth}) at {path}"
            : ex.Message;
}
