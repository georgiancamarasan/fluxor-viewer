using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Every Session seen since the Viewer started, kept in memory as received.
/// IN-3 adds status and Clear; IN-4 replaces raw storage with reconstructed Feature state.
/// </summary>
public sealed class SessionRegistry : ISessionRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> sessions = [];
    private readonly Dictionary<string, Guid> connections = [];

    public void SnapshotReceived(string connectionId, Snapshot snapshot)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(snapshot.SessionId, out var session))
                sessions[snapshot.SessionId] = session = new Entry(snapshot);
            session.Snapshots.Add(snapshot);
            session.ConnectionId = connectionId;
            connections[connectionId] = snapshot.SessionId;
        }
    }

    public void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions)
    {
        lock (gate)
        {
            if (connections.TryGetValue(connectionId, out var sessionId))
                sessions[sessionId].Actions.AddRange(actions);
        }
    }

    public void ConnectionClosed(string connectionId)
    {
        lock (gate)
        {
            if (!connections.Remove(connectionId, out var sessionId))
                return;
            // A reconnect may already have moved the Session to a newer connection.
            var session = sessions[sessionId];
            if (session.ConnectionId == connectionId)
                session.ConnectionId = null;
        }
    }

    /// <summary>A copy of every Session, safe to read while ingestion continues.</summary>
    public IReadOnlyList<ReceivedSession> GetSessions()
    {
        lock (gate)
            return sessions.Values.Select(s => s.ToView()).ToList();
    }

    public ReceivedSession? Find(Guid sessionId)
    {
        lock (gate)
            return sessions.TryGetValue(sessionId, out var session) ? session.ToView() : null;
    }

    private sealed class Entry(Snapshot first)
    {
        public Snapshot First { get; } = first;
        public List<Snapshot> Snapshots { get; } = [];
        public List<ActionRecorded> Actions { get; } = [];
        public string? ConnectionId { get; set; }

        public ReceivedSession ToView() => new(
            First.SessionId, First.AppName, First.Runtime, First.StartedAt, ConnectionId, [.. Snapshots], [.. Actions]);
    }
}

/// <summary>A Session as received so far.</summary>
/// <param name="ConnectionId">The hub connection currently reporting it, or null once that closed.</param>
public sealed record ReceivedSession(
    Guid SessionId,
    string AppName,
    Runtime Runtime,
    DateTimeOffset StartedAt,
    string? ConnectionId,
    IReadOnlyList<Snapshot> Snapshots,
    IReadOnlyList<ActionRecorded> Actions);
