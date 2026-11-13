using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace FluxorViewer.Middleware;

/// <summary>
/// Keeps each message under the Viewer's limit (<see cref="JsonLimits.MaxMessageSize"/>): measures messages as the hub
/// connection sends them, splits Actions between calls, and shrinks a message that's over it on its own.
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

    private static readonly JavaScriptEncoder Encoder = Options.Encoder ?? JavaScriptEncoder.Default;

    private static readonly Shape<Snapshot> SnapshotShape = new(
        snapshot => snapshot.Features.Select(f => new MessageValue(ViewerJson.FeatureSubject(f.Name), f.StateJson)).ToList(),
        (snapshot, json) => snapshot with { Features = snapshot.Features.Select((f, i) => f with { StateJson = json[i] }).ToList() });

    // An Action's Payload comes first, then its Changed Features' states.
    private static readonly Shape<ActionRecorded> ActionShape = new(
        action => action.ChangedFeatures.Select(f => new MessageValue(ViewerJson.FeatureSubject(f.Name), f.StateJson))
            .Prepend(new MessageValue(ViewerJson.ActionSubject(action.ActionType), action.Payload)).ToList(),
        (action, json) => action with
        {
            Payload = json[0],
            ChangedFeatures = action.ChangedFeatures.Select((f, i) => f with { StateJson = json[i + 1] }).ToList(),
        });

    /// <summary>
    /// <paramref name="actions"/> in order, in as few parts as fit <see cref="Budget"/>; an Action too large on its own
    /// goes in a part by itself. Parts only divide what they're given, so never hold more Actions than it.
    /// </summary>
    public static IEnumerable<IReadOnlyList<ActionRecorded>> Split(IEnumerable<ActionRecorded> actions)
    {
        var part = new List<ActionRecorded>();
        long size = 0;
        foreach (var action in actions)
        {
            var actionSize = Size(action, ActionShape) + 1; // and the comma between it and the next
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
    /// <exception cref="InvalidOperationException">It's still over with every Feature state left out.</exception>
    public static Snapshot Fit(Snapshot snapshot, ViewerJson json) => Fit(snapshot, SnapshotShape, json);

    /// <summary>
    /// <paramref name="action"/>, or if it's over <see cref="Budget"/> on its own, a copy with its largest values (its
    /// Payload and Changed Features alike) left out (as Serialisation errors from <paramref name="json"/>, which logs
    /// them) until it fits.
    /// </summary>
    /// <exception cref="InvalidOperationException">It's still over with every value left out.</exception>
    public static ActionRecorded Fit(ActionRecorded action, ViewerJson json) => Fit(action, ActionShape, json);

    private static T Fit<T>(T message, Shape<T> shape, ViewerJson json)
    {
        var size = Size(message, shape);
        return size <= Budget ? message : shape.With(message, LeaveOutLargest(size, shape.Values(message), json));
    }

    /// <summary>The JSON of <paramref name="values"/>, the largest left out until a message of <paramref name="size"/> fits.</summary>
    private static string[] LeaveOutLargest(long size, IReadOnlyList<MessageValue> values, ViewerJson json)
    {
        var fitted = values.Select(v => v.Json).ToArray();
        var largestFirst = values.Select((value, index) => (Index: index, Size: EncodedSize(value.Json))).OrderByDescending(v => v.Size);
        foreach (var (index, valueSize) in largestFirst)
        {
            if (size <= Budget)
                break;
            fitted[index] = json.LeftOut(values[index].Subject, values[index].Json);
            size += EncodedSize(fitted[index]) - valueSize;
        }

        return size <= Budget
            ? fitted
            : throw new InvalidOperationException(
                $"The message is over the Viewer's {ByteSize.Readable(JsonLimits.MaxMessageSize)} limit even with every Payload and Feature state left out.");
    }

    /// <summary>
    /// How many bytes <paramref name="message"/> takes in <see cref="Options"/>: its envelope, serialised with its values
    /// emptied, plus each value's encoded size, so no value is copied to measure it.
    /// </summary>
    private static long Size<T>(T message, Shape<T> shape)
    {
        var values = shape.Values(message);
        var envelope = shape.With(message, values.Select(_ => "").ToList());
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options).LongLength + values.Sum(v => EncodedSize(v.Json));
    }

    /// <summary>How many UTF-8 bytes <paramref name="value"/> takes inside a JSON string in <see cref="Options"/>, encoded a chunk at a time.</summary>
    private static long EncodedSize(string value)
    {
        var chunk = ArrayPool<char>.Shared.Rent(16 * 1024);
        try
        {
            long size = 0;
            var rest = value.AsSpan();
            while (true)
            {
                var status = Encoder.Encode(rest, chunk, out var read, out var written, isFinalBlock: true);
                size += Encoding.UTF8.GetByteCount(chunk.AsSpan(0, written));
                rest = rest[read..];
                if (status != OperationStatus.DestinationTooSmall)
                    return size;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chunk);
        }
    }

    /// <summary>A Payload or Feature state in a message, and what it belongs to, for the log.</summary>
    private sealed record MessageValue(string Subject, string Json);

    /// <summary>How to read a kind of message's values, in order, and copy it with new ones.</summary>
    private sealed record Shape<T>(Func<T, IReadOnlyList<MessageValue>> Values, Func<T, IReadOnlyList<string>, T> With);
}
