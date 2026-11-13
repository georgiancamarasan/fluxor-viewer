using System.Text.Json;
using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace FluxorViewer.Middleware;

/// <summary>
/// The JSON the hub connection sends messages in, and their sizes in it, so the sender can keep each message under the
/// Viewer's limit (<see cref="JsonLimits.MaxMessageSize"/>).
/// </summary>
internal static class WireJson
{
    /// <summary>Room left in each message for the hub's framing around the Snapshot or Actions it carries.</summary>
    internal const int Headroom = 64 * 1024;

    /// <summary>The most a Snapshot, or one call's Actions together, may take in <see cref="Options"/>.</summary>
    public const long Budget = JsonLimits.MaxMessageSize - Headroom;

    /// <summary>
    /// The payload options SignalR's JSON protocol sends with by default, which the hub connection uses unchanged. Their
    /// escaping is minimal (<c>"</c> as <c>\"</c>, non-ASCII as UTF-8), so a Payload or Feature state takes about its own
    /// size on the wire.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new JsonHubProtocolOptions().PayloadSerializerOptions;

    /// <summary>How many bytes <paramref name="message"/> takes in <see cref="Options"/>, measured without keeping them.</summary>
    public static long Size<T>(T message)
    {
        var counter = new CappedBuffer(0);
        using (var writer = new Utf8JsonWriter(counter, new JsonWriterOptions { Encoder = Options.Encoder }))
            JsonSerializer.Serialize(writer, message, Options);
        return counter.Length;
    }

    /// <summary>
    /// <paramref name="actions"/> in order, in as few parts as fit <see cref="Budget"/>, at most
    /// <see cref="ActionBuffer.MaxBatchSize"/> each. An Action too large on its own goes in a part by itself.
    /// </summary>
    public static IEnumerable<IReadOnlyList<ActionRecorded>> Split(IEnumerable<ActionRecorded> actions)
    {
        var part = new List<ActionRecorded>();
        long size = 0;
        foreach (var action in actions)
        {
            var actionSize = Size(action) + 1; // and the comma between it and the next
            if (part.Count > 0 && size + actionSize > Budget)
            {
                yield return part;
                (part, size) = ([], 0);
            }

            part.Add(action);
            size += actionSize;
        }

        if (part.Count > 0)
            yield return part;
    }

    /// <summary>
    /// <paramref name="snapshot"/>, or if it's over <see cref="Budget"/>, a copy with its largest Feature states left out
    /// (as Serialisation errors from <paramref name="json"/>, which logs them) until it fits.
    /// </summary>
    public static Snapshot Fit(Snapshot snapshot, ViewerJson json)
    {
        var size = Size(snapshot);
        if (size <= Budget)
            return snapshot;
        var states = LeaveOutLargest(size, snapshot.Features.Select(f => new Value($"Feature {f.Name}", f.StateJson)).ToList(), json);
        return snapshot with { Features = snapshot.Features.Select((f, i) => f with { StateJson = states[i] }).ToList() };
    }

    /// <summary>
    /// <paramref name="action"/>, or if it's over <see cref="Budget"/> on its own, a copy with its largest values (its
    /// Payload and Changed Features alike) left out (as Serialisation errors from <paramref name="json"/>, which logs them)
    /// until it fits.
    /// </summary>
    public static ActionRecorded Fit(ActionRecorded action, ViewerJson json)
    {
        var size = Size(action);
        if (size <= Budget)
            return action;
        var values = action.ChangedFeatures.Select(f => new Value($"Feature {f.Name}", f.StateJson))
            .Prepend(new Value($"Action {action.ActionType}", action.Payload)).ToList();
        var fitted = LeaveOutLargest(size, values, json);
        return action with
        {
            Payload = fitted[0],
            ChangedFeatures = action.ChangedFeatures.Select((f, i) => f with { StateJson = fitted[i + 1] }).ToList(),
        };
    }

    /// <summary>The JSON of <paramref name="values"/>, the largest left out until a message of <paramref name="size"/> fits.</summary>
    private static string[] LeaveOutLargest(long size, IReadOnlyList<Value> values, ViewerJson json)
    {
        var fitted = values.Select(v => v.Json).ToArray();
        foreach (var (index, valueSize) in values.Select((v, i) => (i, Size(v.Json))).OrderByDescending(v => v.Item2))
        {
            if (size <= Budget)
                break;
            fitted[index] = json.LeftOut(values[index].Subject, values[index].Json);
            size += Size(fitted[index]) - valueSize;
        }

        return fitted;
    }

    /// <summary>A Payload or Feature state in a message, and what it belongs to, for the log.</summary>
    private sealed record Value(string Subject, string Json);
}
