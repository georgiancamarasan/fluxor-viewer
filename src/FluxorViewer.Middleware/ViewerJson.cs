using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Serialises one Payload or Feature state for the Viewer, sending a Serialisation error, exactly
/// <c>{"$error": "message"}</c>, in its place if it won't serialise. Each failing Action type or Feature is logged at
/// Warning once per process (a Blazor Server app has many Sessions), then at Debug.
/// </summary>
internal sealed class ViewerJson(int maxDepth, ILogger logger)
{
    /// <summary>Longer Serialisation error messages are cut to this and end in <c>…</c>.</summary>
    internal const int MaxErrorLength = 500;

    private static readonly ConcurrentDictionary<string, bool> Warned = new();

    private readonly JsonSerializerOptions options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = maxDepth,
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
        try
        {
            return JsonSerializer.Serialize(value, type, options);
        }
        catch (Exception ex)
        {
            var level = Warned.TryAdd(subject, true) ? LogLevel.Warning : LogLevel.Debug;
            logger.Log(level, ex, "FluxorViewer: could not serialise {Subject}; sent a Serialisation error instead.", subject);
            return Error(ex);
        }
    }

    private string Error(Exception ex)
    {
        var message = Describe(ex);
        if (message.Length > MaxErrorLength)
            message = message[..(MaxErrorLength - 1)] + "…";
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["$error"] = message });
    }

    private string Describe(Exception ex) =>
        // With IgnoreCycles on, this "possible object cycle" can only be depth, and its advice (ReferenceHandler.Preserve)
        // isn't the developer's to take. System.Text.Json's messages aren't localised, so matching the text is safe.
        ex is JsonException { Path: var path } && ex.Message.Contains($"maximum allowed depth of {maxDepth}")
            ? $"Nested deeper than MaxDepth ({maxDepth}) at {path}"
            : ex.Message;
}
