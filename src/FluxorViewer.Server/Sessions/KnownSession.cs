using System.Collections.Frozen;
using System.Collections.Immutable;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// One Session the Viewer has seen: its connection, current Feature states and History. Not thread-safe: the
/// <see cref="SessionRegistry"/>'s lock guards it.
/// </summary>
internal sealed class KnownSession(Snapshot firstSnapshot, long seenOrder, HistoryLimit limit)
{
    private readonly List<HistoryEntry> history = [];
    private long lastId;
    private bool receivedAny;
    private bool warnedOlderSnapshot;

    public Guid SessionId => firstSnapshot.SessionId;

    /// <summary>Orders Sessions by when the Viewer first saw them.</summary>
    public long SeenOrder => seenOrder;

    /// <summary>The hub connection currently reporting the Session; null while it's disconnected.</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>Feature states after the latest Action or Snapshot.</summary>
    public ImmutableDictionary<string, FeatureState> Current { get; private set; } = FeatureMap.Empty;

    /// <summary>The Sequence of the latest Action or Snapshot received.</summary>
    public long LastSequence { get; private set; }

    /// <summary>Whether entries were discarded to keep the History within its limit, since the last Clear.</summary>
    public bool HasDiscarded { get; private set; }

    /// <summary>
    /// Takes a Snapshot arriving on <paramref name="connectionId"/>, which becomes the Session's connection: appends a
    /// Resync when it doesn't follow on from what was received, and makes its Feature states current.
    /// </summary>
    public SnapshotTaken Take(Snapshot snapshot, string connectionId, DateTimeOffset receivedAt)
    {
        long? lastReceived = receivedAny ? LastSequence : null;
        var states = FeatureMap.From(snapshot.Features);
        var loss = LossBefore(snapshot, lastReceived);
        if (loss is var (lost, firstLost))
            Append(new ResyncEntry(NextId(), snapshot.LastSequence, receivedAt, lost, firstLost, states));

        Current = states;
        LastSequence = snapshot.LastSequence;
        ConnectionId = connectionId;
        receivedAny = true;
        return new SnapshotTaken(loss is not null, NoteFirstOlderSnapshot(snapshot, lastReceived));
    }

    /// <summary>
    /// Appends the Action with the state before it and after it; only its Changed Features are new. An Action at or before
    /// the last received Sequence is a duplicate and ignored; returns whether it was appended.
    /// </summary>
    public bool Record(ActionRecorded action)
    {
        if (action.Sequence <= LastSequence)
            return false;
        var before = Current;
        var after = before.SetItems(action.ChangedFeatures.Select(f => KeyValuePair.Create(f.Name, f)));
        Append(new ActionEntry(
            NextId(),
            action.Sequence,
            action.Timestamp,
            action.ActionType,
            action.Payload,
            action.ChangedFeatures.Select(f => f.Name).ToFrozenSet(),
            before,
            after));
        Current = after;
        LastSequence = action.Sequence;
        return true;
    }

    /// <summary>When the Session last disconnected, as a position among the registry's disconnects; lower is longer ago.</summary>
    public long DisconnectOrder { get; private set; }

    /// <summary>The Session's connection closed; it may reconnect.</summary>
    /// <param name="disconnectOrder">This disconnect's position among the registry's disconnects.</param>
    public void Disconnect(long disconnectOrder)
    {
        ConnectionId = null;
        DisconnectOrder = disconnectOrder;
    }

    /// <summary>Empties the History; the current Feature states and last Sequence stay as the new starting point.</summary>
    public void ClearHistory()
    {
        history.Clear();
        HasDiscarded = false;
    }

    /// <summary>A copy for readers, safe to use while ingestion goes on.</summary>
    public ReceivedSession ToView() => new(
        SessionId, firstSnapshot.AppName, firstSnapshot.Runtime, firstSnapshot.StartedAt, ConnectionId, [.. history], Current, LastSequence)
    {
        HasDiscarded = HasDiscarded,
    };

    /// <summary>
    /// How many Actions were lost before <paramref name="snapshot"/> and the first of them; null when nothing was lost.
    /// <paramref name="lastReceived"/> is null for a new Session: its Actions before its first Snapshot predate this
    /// Viewer, so only the dropped ones were lost. A Snapshot older than what was received (ADR 0005) lost an unknown number.
    /// </summary>
    private static (long? Lost, long? FirstLost)? LossBefore(Snapshot snapshot, long? lastReceived)
    {
        var (lost, firstUnreceived) = lastReceived switch
        {
            null => (snapshot.DroppedCount, 1),
            { } received when snapshot.LastSequence < received => ((long?)null, received + 1),
            { } received => (Math.Max(snapshot.DroppedCount, snapshot.LastSequence - received), received + 1),
        };
        return lost is 0 ? null : (lost, FirstLost(lost, firstUnreceived, snapshot.LastSequence));
    }

    // The lost Actions end at the Snapshot; they can't start before the first one this Viewer didn't receive.
    private static long? FirstLost(long? lost, long firstUnreceived, long lastSequence) =>
        lost is { } count && Math.Max(firstUnreceived, lastSequence - count + 1) is var first && first <= lastSequence ? first : null;

    /// <summary>
    /// The first time this Session sends a Snapshot older than what was received, notes it and returns the Sequence already
    /// received; otherwise null.
    /// </summary>
    private long? NoteFirstOlderSnapshot(Snapshot snapshot, long? lastReceived)
    {
        if (lastReceived is not { } received || snapshot.LastSequence >= received || warnedOlderSnapshot)
            return null;
        warnedOlderSnapshot = true;
        return received;
    }

    /// <summary>Appends an entry to the History, discarding the oldest if that takes it past the limit.</summary>
    private void Append(HistoryEntry entry)
    {
        history.Add(entry);
        if (history.Count <= limit.Value)
            return;
        history.RemoveAt(0);
        HasDiscarded = true;
    }

    /// <summary>The Id for the next entry appended to the History; never reused, even after Clear.</summary>
    private long NextId() => ++lastId;
}

/// <summary>What taking a Snapshot did to a Session.</summary>
/// <param name="ResyncAppended">Whether a Resync was appended to the History.</param>
/// <param name="FirstOlderThan">
/// The Sequence already received, the first time the Session sends a Snapshot older than it (ADR 0005); otherwise null.
/// </param>
internal readonly record struct SnapshotTaken(bool ResyncAppended, long? FirstOlderThan);
