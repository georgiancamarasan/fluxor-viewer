using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Every Session seen since the Viewer started, with its status and what it has reported, kept in memory.
/// IN-4 replaces raw storage with reconstructed Feature state.
/// </summary>
/// <remarks>
/// One lock guards everything: hub calls from many Sessions, closes and Clears are serialised, and readers get copies.
/// </remarks>
public sealed class SessionRegistry : ISessionRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> sessions = [];
    private readonly Dictionary<string, Guid> connections = [];
    private long seen;

    public void SnapshotReceived(string connectionId, Snapshot snapshot)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(snapshot.SessionId, out var session))
                sessions[snapshot.SessionId] = session = new Entry(snapshot, ++seen);
            session.Snapshots.Add(snapshot);
            session.ActionsAtLatestSnapshot = session.Actions.Count;
            session.ConnectionId = connectionId;
            connections[connectionId] = snapshot.SessionId;
        }
    }

    public void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions)
    {
        lock (gate)
        {
            // Actions from a connection the Session has moved away from are already covered by the newer Snapshot.
            if (Current(connectionId) is { } session)
                session.Actions.AddRange(actions);
        }
    }

    public void ConnectionClosed(string connectionId)
    {
        lock (gate)
        {
            // A late close from a replaced connection leaves the Session connected through the newer one.
            if (Current(connectionId) is { } session)
                session.ConnectionId = null;
            connections.Remove(connectionId);
        }
    }

    /// <summary>
    /// Discards a Session's history. A disconnected Session is removed; a connected one keeps its current Feature
    /// states and last Sequence as its new starting point and goes on recording. Unknown Sessions are ignored.
    /// </summary>
    public void Clear(Guid sessionId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
                return;
            if (session.ConnectionId is null)
            {
                sessions.Remove(sessionId);
                return;
            }

            var startingPoint = session.CurrentState();
            session.Snapshots.Clear();
            session.Actions.Clear();
            session.Snapshots.Add(startingPoint);
            session.ActionsAtLatestSnapshot = 0;
        }
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
        public List<Snapshot> Snapshots { get; } = [];
        public List<ActionRecorded> Actions { get; } = [];
        public int ActionsAtLatestSnapshot { get; set; }
        public string? ConnectionId { get; set; }

        /// <summary>The latest Snapshot with the Changed Features of every Action after it applied.</summary>
        public Snapshot CurrentState()
        {
            var latest = Snapshots[^1];
            var features = latest.Features.ToDictionary(f => f.Name);
            var lastSequence = latest.LastSequence;
            foreach (var action in Actions.Skip(ActionsAtLatestSnapshot).Where(a => a.Sequence > latest.LastSequence))
            {
                foreach (var changed in action.ChangedFeatures)
                    features[changed.Name] = changed;
                lastSequence = Math.Max(lastSequence, action.Sequence);
            }

            return latest with { LastSequence = lastSequence, DroppedCount = 0, Features = [.. features.Values] };
        }

        public ReceivedSession ToView() => new(
            First.SessionId, First.AppName, First.Runtime, First.StartedAt, ConnectionId, [.. Snapshots], [.. Actions]);
    }
}

/// <summary>Whether the Viewer currently has a connection for a Session. Disconnected Sessions may still reconnect.</summary>
public enum SessionStatus
{
    Connected,
    Disconnected,
}

/// <summary>A Session as received so far.</summary>
/// <param name="ConnectionId">The hub connection currently reporting it, or null once that closed.</param>
/// <param name="Snapshots">Snapshots received; after a Clear, just the starting point the Clear left.</param>
public sealed record ReceivedSession(
    Guid SessionId,
    string AppName,
    Runtime Runtime,
    DateTimeOffset StartedAt,
    string? ConnectionId,
    IReadOnlyList<Snapshot> Snapshots,
    IReadOnlyList<ActionRecorded> Actions)
{
    /// <summary>The first four hex characters of the SessionId; a display label only, and not unique.</summary>
    public string ShortId => SessionId.ToString("N")[..4];

    public SessionStatus Status => ConnectionId is null ? SessionStatus.Disconnected : SessionStatus.Connected;
}
