using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Tests;

/// <summary>
/// Protocol messages as a Host app's middleware sends them, for an app whose only Feature is a Counter that each
/// increment sets to the increment's Sequence. Vary a Snapshot with <c>with</c>.
/// </summary>
internal static class Messages
{
    /// <summary>The Counter Feature's state at <paramref name="count"/>.</summary>
    public static FeatureState Counter(long count) => new("Counter", "App.CounterState", $$"""{"Count":{{count}}}""");

    /// <summary>A Session's first Snapshot: TestApp on Server, nothing sent before it, the Counter at 0.</summary>
    public static Snapshot NewSnapshot(Guid sessionId) => new(
        ProtocolVersion.Current, sessionId, "TestApp", Runtime.Server, DateTimeOffset.UtcNow, LastSequence: 0, DroppedCount: 0, [Counter(0)]);

    /// <summary>A Snapshot taken after Action <paramref name="lastSequence"/>, with the Counter at that count.</summary>
    public static Snapshot SnapshotAfter(Guid sessionId, long lastSequence) =>
        NewSnapshot(sessionId) with { LastSequence = lastSequence, Features = [Counter(lastSequence)] };

    /// <summary>An increment that sets the Counter to its <paramref name="sequence"/>.</summary>
    public static ActionRecorded Increment(Guid sessionId, long sequence) =>
        new(sessionId, sequence, DateTimeOffset.UtcNow, "App.IncrementAction", "{}", [Counter(sequence)]);

    /// <summary>Increments <paramref name="from"/> to <paramref name="to"/>, both included, as one batch.</summary>
    public static ActionRecorded[] Increments(Guid sessionId, long from, long to) =>
        [.. Enumerable.Range(0, (int)(to - from + 1)).Select(i => Increment(sessionId, from + i))];
}
