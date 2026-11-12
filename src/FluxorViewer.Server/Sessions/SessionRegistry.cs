using System.Collections.Frozen;
using System.Collections.Immutable;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Every Session seen since the Viewer started, with its status and History, kept in memory.
/// </summary>
/// <remarks>
/// One lock guards everything: hub calls from many Sessions, closes and Clears are serialised, and readers get copies.
/// <see cref="Changed"/> is raised after the lock is released.
/// </remarks>
public sealed class SessionRegistry(ILogger<SessionRegistry> logger, TimeProvider time) : ISessionRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> sessions = [];
    private readonly Dictionary<string, Guid> connections = [];
    private long seen;

    /// <summary>
    /// Raised at most once per kind of change per call, after the change is made and outside the lock, synchronously on
    /// the calling thread (a hub invocation or a Clear). Handlers must not block and must marshal to their own context
    /// (in Blazor, <c>InvokeAsync</c>). A handler that throws is logged and doesn't affect ingestion or other handlers.
    /// </summary>
    public event Action<SessionChange>? Changed;

    public void SnapshotReceived(string connectionId, Snapshot snapshot)
    {
        List<SessionChange> changes = [];
        lock (gate)
        {
            long? lost;
            // The lost Actions end at the Snapshot; they can't start before the first one this Viewer didn't receive.
            long firstUnreceived;
            if (!sessions.TryGetValue(snapshot.SessionId, out var session))
            {
                sessions[snapshot.SessionId] = session = new Entry(snapshot, ++seen);
                changes.Add(new SessionAdded(snapshot.SessionId));
                // Actions before a new Session's first Snapshot predate this Viewer; only the dropped ones were lost.
                lost = snapshot.DroppedCount;
                firstUnreceived = 1;
            }
            else
            {
                if (session.ConnectionId is null)
                    changes.Add(new SessionStatusChanged(snapshot.SessionId, SessionStatus.Connected));
                lost = Math.Max(snapshot.DroppedCount, snapshot.LastSequence - session.LastSequence);
                firstUnreceived = session.LastSequence + 1;
                if (snapshot.LastSequence < session.LastSequence)
                {
                    // The middleware never sends one (ADR 0005); if it does, take it without rewinding the History.
                    lost = null;
                    if (!session.WarnedOlderSnapshot)
                    {
                        session.WarnedOlderSnapshot = true;
                        logger.LogWarning(
                            "Session {SessionId} sent a Snapshot at Sequence {LastSequence}, older than the {Received} already received; recorded as a Resync.",
                            snapshot.SessionId, snapshot.LastSequence, session.LastSequence);
                    }
                }
            }

            if (lost is null or > 0)
            {
                long? firstLost = lost is { } n ? Math.Max(firstUnreceived, snapshot.LastSequence - n + 1) : null;
                session.History.Add(new ResyncEntry(
                    session.NextId(),
                    snapshot.LastSequence,
                    time.GetLocalNow(),
                    lost,
                    firstLost <= snapshot.LastSequence ? firstLost : null,
                    FeatureMap.From(snapshot.Features)));
                changes.Add(new EntriesAppended(snapshot.SessionId, 1));
            }

            session.Current = FeatureMap.From(snapshot.Features);
            session.LastSequence = snapshot.LastSequence;
            session.ConnectionId = connectionId;
            connections[connectionId] = snapshot.SessionId;
        }
        foreach (var change in changes)
            Raise(change);
    }

    public void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions)
    {
        SessionChange? change = null;
        lock (gate)
        {
            // Actions from a connection the Session has moved away from are already covered by the newer Snapshot.
            if (Current(connectionId) is not { } session)
                return;
            var before = session.History.Count;
            foreach (var action in actions)
                session.Record(action);
            if (session.History.Count > before)
                change = new EntriesAppended(session.First.SessionId, session.History.Count - before);
        }
        Raise(change);
    }

    public void ConnectionClosed(string connectionId)
    {
        SessionChange? change = null;
        lock (gate)
        {
            // A late close from a replaced connection leaves the Session connected through the newer one.
            if (Current(connectionId) is { } session)
            {
                session.ConnectionId = null;
                change = new SessionStatusChanged(session.First.SessionId, SessionStatus.Disconnected);
            }
            connections.Remove(connectionId);
        }
        Raise(change);
    }

    /// <summary>
    /// Discards a Session's History. A disconnected Session is removed; a connected one keeps its current Feature
    /// states and last Sequence as its new starting point and goes on recording. Unknown Sessions are ignored.
    /// </summary>
    public void Clear(Guid sessionId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
                return;
            if (session.ConnectionId is null)
                sessions.Remove(sessionId);
            else
                session.History.Clear();
        }
        Raise(new SessionCleared(sessionId));
    }

    /// <summary>A copy of every Session in the order the Viewer first saw them, safe to read while ingestion continues.</summary>
    public IReadOnlyList<ReceivedSession> GetSessions()
    {
        lock (gate)
            return sessions.Values.OrderBy(s => s.Seen).Select(s => s.ToView()).ToList();
    }

    public ReceivedSession? Find(Guid sessionId)
    {
        lock (gate)
            return sessions.TryGetValue(sessionId, out var session) ? session.ToView() : null;
    }

    private void Raise(SessionChange? change)
    {
        if (change is null || Changed is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<SessionChange>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A {Change} handler threw; ingestion continues.", change.GetType().Name);
            }
        }
    }

    /// <summary>The Session <paramref name="connectionId"/> reports, if that connection is still the Session's current one.</summary>
    private Entry? Current(string connectionId) =>
        connections.TryGetValue(connectionId, out var sessionId)
        && sessions.TryGetValue(sessionId, out var session)
        && session.ConnectionId == connectionId
            ? session
            : null;

    private sealed class Entry(Snapshot first, long seen)
    {
        public long Seen { get; } = seen;
        public Snapshot First { get; } = first;
        public List<HistoryEntry> History { get; } = [];
        public ImmutableDictionary<string, FeatureState> Current { get; set; } = FeatureMap.Empty;
        public long LastSequence { get; set; }
        public string? ConnectionId { get; set; }
        public bool WarnedOlderSnapshot { get; set; }

        private long lastId;

        /// <summary>The Id for the next entry appended to the History; never reused, even after Clear.</summary>
        public long NextId() => ++lastId;

        /// <summary>
        /// Appends the Action with the state before it and after it; only its Changed Features are new. An Action at or
        /// before the last received Sequence is a duplicate and ignored.
        /// </summary>
        public void Record(ActionRecorded action)
        {
            if (action.Sequence <= LastSequence)
                return;
            var before = Current;
            var after = before.SetItems(action.ChangedFeatures.Select(f => KeyValuePair.Create(f.Name, f)));
            History.Add(new ActionEntry(
                NextId(),
                action.Sequence,
                action.Timestamp,
                action.ActionType,
                action.ActionJson,
                action.ChangedFeatures.Select(f => f.Name).ToFrozenSet(),
                before,
                after));
            Current = after;
            LastSequence = action.Sequence;
        }

        public ReceivedSession ToView() => new(
            First.SessionId, First.AppName, First.Runtime, First.StartedAt, ConnectionId, [.. History], Current, LastSequence);
    }
}

/// <summary>Feature states keyed by Feature name. Maps are immutable and share unchanged states.</summary>
internal static class FeatureMap
{
    public static readonly ImmutableDictionary<string, FeatureState> Empty = ImmutableDictionary<string, FeatureState>.Empty;

    public static ImmutableDictionary<string, FeatureState> From(IEnumerable<FeatureState> features) =>
        Empty.SetItems(features.Select(f => KeyValuePair.Create(f.Name, f)));
}

/// <summary>Whether the Viewer currently has a connection for a Session. Disconnected Sessions may still reconnect.</summary>
public enum SessionStatus
{
    Connected,
    Disconnected,
}

/// <summary>One point in a Session's History, with the Feature states after it.</summary>
/// <param name="Id">
/// Identifies the entry within its Session, increasing in the order entries were appended and never reused. Unlike the
/// Sequence, it's unique: a Resync shares its Sequence with the Action before it, and after an older Snapshot the
/// History may hold Actions with the same Sequence twice.
/// </param>
/// <param name="Sequence">The Action's Sequence, or the Snapshot's <c>LastSequence</c> for a Resync.</param>
public abstract record HistoryEntry(long Id, long Sequence, ImmutableDictionary<string, FeatureState> After);

/// <summary>An Action in the History.</summary>
/// <param name="ChangedFeatures">Names of the Features whose state this Action replaced.</param>
/// <param name="Before">Feature states before this Action. Unchanged Features share their instances with <paramref name="After"/>.</param>
public sealed record ActionEntry(
    long Id,
    long Sequence,
    DateTimeOffset Timestamp,
    string ActionType,
    string ActionJson,
    IReadOnlySet<string> ChangedFeatures,
    ImmutableDictionary<string, FeatureState> Before,
    ImmutableDictionary<string, FeatureState> After) : HistoryEntry(Id, Sequence, After);

/// <summary>A Snapshot that didn't follow on from what the Viewer already had.</summary>
/// <param name="Sequence">The Snapshot's <c>LastSequence</c>: the Resync sits just after the last Action it covers.</param>
/// <param name="ReceivedAt">When the Viewer received the Snapshot.</param>
/// <param name="Lost">How many Actions the Viewer missed before it; null when the Snapshot was older than what the Viewer had.</param>
/// <param name="FirstLost">
/// The Sequence of the first lost Action, the rest running up to <paramref name="Sequence"/>; null when the Viewer can't
/// tell which were lost.
/// </param>
/// <param name="After">The Snapshot's Feature states.</param>
public sealed record ResyncEntry(
    long Id,
    long Sequence,
    DateTimeOffset ReceivedAt,
    long? Lost,
    long? FirstLost,
    ImmutableDictionary<string, FeatureState> After) : HistoryEntry(Id, Sequence, After);

/// <summary>A Session as received so far.</summary>
/// <param name="ConnectionId">The hub connection currently reporting it, or null once that closed.</param>
/// <param name="Current">Feature states after the latest Action or Snapshot; kept when the History is cleared.</param>
/// <param name="LastSequence">The Sequence of the latest Action or Snapshot received.</param>
public sealed record ReceivedSession(
    Guid SessionId,
    string AppName,
    Runtime Runtime,
    DateTimeOffset StartedAt,
    string? ConnectionId,
    IReadOnlyList<HistoryEntry> History,
    ImmutableDictionary<string, FeatureState> Current,
    long LastSequence)
{
    /// <summary>The first four hex characters of the SessionId; a display label only, and not unique.</summary>
    public string ShortId => SessionId.ToString("N")[..4];

    public SessionStatus Status => ConnectionId is null ? SessionStatus.Disconnected : SessionStatus.Connected;

    public IEnumerable<ActionEntry> Actions => History.OfType<ActionEntry>();
}
