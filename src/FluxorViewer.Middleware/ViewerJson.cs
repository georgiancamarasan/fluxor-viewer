using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Serialises one Payload or Feature state for the Viewer, sending a Serialisation error, exactly
/// <c>{"$error": "message"}</c>, in its place if it won't serialise, its JSON is over <c>MaxValueSize</c>, or it's left
/// out to keep its message under the Viewer's limit. Each problem is logged at Warning once per process for each Action
/// type or Feature it happens to (a Blazor Server app has many Sessions), then at Debug.
/// </summary>
internal sealed class ViewerJson(FluxorViewerOptions options, ILogger logger)
{
    /// <summary>Longer Serialisation error messages are cut to this and end in <c>…</c>.</summary>
    internal const int MaxErrorLength = 500;

    // Each problem (its log message) with each subject is logged at Warning once.
    private static readonly ConcurrentDictionary<(string Message, string Subject), bool> Warned = new();

    private readonly JsonSerializerOptions serializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = options.MaxDepth,
    };

    /// <summary>The largest message, in bytes, the Viewer accepts.</summary>
    public int MaxMessageSize => options.MaxMessageSize;

    /// <summary>What the log calls an Action type, by its readable name (<see cref="TypeNames.Readable"/>).</summary>
    public static string ActionSubject(string actionType) => $"Action {actionType}";

    /// <summary>What the log calls a Feature, by its name.</summary>
    public static string FeatureSubject(string featureName) => $"Feature {featureName}";

    public string Payload(object action)
    {
        var type = action.GetType();
        return Serialise(action, type, ActionSubject(TypeNames.Readable(type)));
    }

    public string FeatureState(string featureName, object? state, Type stateType) =>
        Serialise(state, stateType, FeatureSubject(featureName));

    private string Serialise(object? value, Type type, string subject)
    {
        try
        {
            var buffer = Write(value, type);
            if (buffer.IsOverCap)
                return OverMaxValueSize(subject, buffer.Length);
            // A write that might have crossed the cap wasn't kept, but it didn't: within the cap, so write it again in full.
            return buffer.IsComplete ? buffer.Text : JsonSerializer.Serialize(value, type, serializerOptions);
        }
        catch (Exception ex)
        {
            Log(subject, ex, "FluxorViewer: could not serialise {Subject}; sent a Serialisation error instead.");
            return Error(Describe(ex));
        }
    }

    private string OverMaxValueSize(string subject, long size)
    {
        Log(subject, null, "FluxorViewer: {Subject} is larger than MaxValueSize; sent a Serialisation error instead.");
        return Error($"Larger than MaxValueSize ({ByteSize.Readable(options.MaxValueSize)}): {ByteSize.Readable(size)}");
    }

    /// <summary>
    /// The Serialisation error sent in place of <paramref name="valueJson"/>, a value of <paramref name="subject"/> (e.g.
    /// <c>Feature Todos</c>) left out to keep its message under the Viewer's limit; logged once per subject at Warning.
    /// The size it names is the value's JSON in UTF-8, as for <c>MaxValueSize</c>, not its encoded size in the message.
    /// </summary>
    public string LeftOut(string subject, string valueJson)
    {
        Log(subject, null, "FluxorViewer: left out {Subject} to keep a message under the Viewer's limit; sent a Serialisation error instead.");
        return Error($"Left out to stay under the Viewer's {ByteSize.Readable(MaxMessageSize)} limit " +
                     $"({ByteSize.Readable(Encoding.UTF8.GetByteCount(valueJson))})");
    }

    private CappedBuffer Write(object? value, Type type)
    {
        var buffer = new CappedBuffer(options.MaxValueSize);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = serializerOptions.MaxDepth });
        JsonSerializer.Serialize(writer, value, type, serializerOptions);
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
        ex is JsonException { Path: var path } && ex.Message.Contains($"maximum allowed depth of {options.MaxDepth}")
            ? $"Nested deeper than MaxDepth ({options.MaxDepth}) at {path}"
            : ex.Message;
}
