using System.Collections.Frozen;
using System.Collections.Immutable;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Every Session seen since the Viewer started, with its status and History, kept in memory.
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

            // IN-5 adds a Resync to the History when Actions were lost; until then the Snapshot just becomes current.
            session.Current = FeatureMap.From(snapshot.Features);
            session.LastSequence = snapshot.LastSequence;
            session.ConnectionId = connectionId;
            connections[connectionId] = snapshot.SessionId;
        }
    }

    public void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions)
    {
        lock (gate)
        {
            // Actions from a connection the Session has moved away from are already covered by the newer Snapshot.
            if (Current(connectionId) is not { } session)
                return;
            foreach (var action in actions)
                session.Record(action);
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
        public List<HistoryEntry> History { get; } = [];
        public ImmutableDictionary<string, FeatureState> Current { get; set; } = FeatureMap.Empty;
        public long LastSequence { get; set; }
        public string? ConnectionId { get; set; }

        /// <summary>Appends the Action with the state before it and after it; only its Changed Features are new.</summary>
        public void Record(ActionRecorded action)
        {
            var before = Current;
            var after = before.SetItems(action.ChangedFeatures.Select(f => KeyValuePair.Create(f.Name, f)));
            History.Add(new ActionEntry(
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
public abstract record HistoryEntry(ImmutableDictionary<string, FeatureState> After);

/// <summary>An Action in the History.</summary>
/// <param name="ChangedFeatures">Names of the Features whose state this Action replaced.</param>
/// <param name="Before">Feature states before this Action. Unchanged Features share their instances with <paramref name="After"/>.</param>
public sealed record ActionEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string ActionType,
    string ActionJson,
    IReadOnlySet<string> ChangedFeatures,
    ImmutableDictionary<string, FeatureState> Before,
    ImmutableDictionary<string, FeatureState> After) : HistoryEntry(After);

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
