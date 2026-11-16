using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Every Session seen since the Viewer started, with its status and History, kept in memory.
/// </summary>
/// <remarks>
/// One lock guards everything: hub calls from many Sessions, closes and Clears are serialised, and readers get copies.
/// <see cref="Changed"/> is raised after the lock is released.
/// </remarks>
public sealed class SessionRegistry(ILogger<SessionRegistry> logger, TimeProvider time, SessionLimits limits) : ISessionRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, KnownSession> sessions = [];
    private readonly Dictionary<string, Guid> connections = [];
    private long seen;
    private long disconnects;
    private bool hasForgotten;

    /// <summary>
    /// Raised at most once per kind of change per call (except <see cref="ClearDisconnected"/>, once per Session it
    /// removes), after the change is made and outside the lock, synchronously on
    /// the calling thread (a hub invocation or a Clear). Handlers must not block and must marshal to their own context
    /// (in Blazor, <c>InvokeAsync</c>). A handler that throws is logged and doesn't affect ingestion or other handlers.
    /// </summary>
    public event Action<SessionChange>? Changed;

    /// <summary>How much the Viewer keeps: entries per Session's History, and disconnected Sessions.</summary>
    public SessionLimits Limits => limits;

    /// <summary>
    /// The Disconnected Session limit once the Viewer has forgotten a Session since it started, so the developer can be
    /// told; null until then.
    /// </summary>
    public DisconnectedSessionLimit? ForgottenBeyond
    {
        get
        {
            lock (gate)
                return hasForgotten ? limits.Disconnected : null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A Snapshot older than what the Session already sent breaks ADR 0005; it's still taken, as a Resync with no lost
    /// count, and logged at Warning once per Session.
    /// </remarks>
    public void SnapshotReceived(string connectionId, Snapshot snapshot)
    {
        List<SessionChange?> changes = [];
        lock (gate)
        {
            var (session, change) = FindOrAdd(snapshot);
            changes.Add(change);
            var taken = session.Take(snapshot, connectionId, time.GetLocalNow());
            connections[connectionId] = snapshot.SessionId;
            if (taken.ResyncAppended)
                changes.Add(new EntriesAppended(snapshot.SessionId, 1));
            if (taken.FirstOlderThan is { } received)
                WarnOlderSnapshot(snapshot, received);
        }
        foreach (var change in changes)
            Raise(change);
    }

    /// <inheritdoc/>
    public void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions)
    {
        SessionChange? change = null;
        lock (gate)
        {
            // Actions from a connection the Session has moved away from are already covered by the newer Snapshot.
            if (ReportedBy(connectionId) is not { } session)
                return;
            var appended = actions.Count(session.Record);
            if (appended > 0)
                change = new EntriesAppended(session.SessionId, appended);
        }
        Raise(change);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A disconnect that takes the disconnected Sessions past their limit forgets the one that disconnected longest
    /// ago, in the same call, raising <see cref="SessionForgotten"/> after <see cref="SessionStatusChanged"/>.
    /// </remarks>
    public void ConnectionClosed(string connectionId)
    {
        List<SessionChange?> changes = [];
        lock (gate)
        {
            // A late close from a replaced connection leaves the Session connected through the newer one.
            if (ReportedBy(connectionId) is { } session)
            {
                changes.Add(Disconnect(session));
                changes.Add(ForgetPastLimit());
            }
            connections.Remove(connectionId);
        }
        foreach (var change in changes)
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
            if (session.IsDisconnected)
                sessions.Remove(sessionId);
            else
                session.ClearHistory();
        }
        Raise(new SessionCleared(sessionId));
    }

    /// <summary>
    /// Removes every Session that is disconnected now, from every Host app, raising <see cref="SessionCleared"/> for each;
    /// connected Sessions are untouched. Whether the Viewer has forgotten a Session (<see cref="ForgottenBeyond"/>) stays as it was.
    /// </summary>
    public void ClearDisconnected()
    {
        List<Guid> removed;
        lock (gate)
        {
            removed = [.. Disconnected().Select(s => s.SessionId)];
            foreach (var sessionId in removed)
                sessions.Remove(sessionId);
        }
        foreach (var sessionId in removed)
            Raise(new SessionCleared(sessionId));
    }

    /// <summary>A copy of every Session in the order the Viewer first saw them, safe to read while ingestion continues.</summary>
    public IReadOnlyList<ReceivedSession> GetSessions()
    {
        lock (gate)
            return sessions.Values.OrderBy(s => s.SeenOrder).Select(s => s.ToView()).ToList();
    }

    /// <summary>A copy of the Session with this id, safe to read while ingestion continues; null if there's none.</summary>
    public ReceivedSession? Find(Guid sessionId)
    {
        lock (gate)
            return sessions.TryGetValue(sessionId, out var session) ? session.ToView() : null;
    }

    /// <summary>The Session the Snapshot is for, added if it's new, and whether that's a new or reconnected Session.</summary>
    private (KnownSession Session, SessionChange? Change) FindOrAdd(Snapshot snapshot)
    {
        if (sessions.TryGetValue(snapshot.SessionId, out var known))
            return (known, known.IsDisconnected ? new SessionStatusChanged(snapshot.SessionId, SessionStatus.Connected) : null);
        var added = sessions[snapshot.SessionId] = new KnownSession(snapshot, ++seen, limits.History);
        return (added, new SessionAdded(snapshot.SessionId));
    }

    /// <summary>Marks the Session disconnected as the latest of the disconnects so far.</summary>
    /// <returns>The status change to raise.</returns>
    private SessionStatusChanged Disconnect(KnownSession session)
    {
        session.Disconnect(++disconnects);
        return new SessionStatusChanged(session.SessionId, SessionStatus.Disconnected);
    }

    /// <summary>
    /// Forgets the Session that disconnected longest ago if more are disconnected than the limit allows, records that
    /// the Viewer has forgotten (<see cref="ForgottenBeyond"/>) and logs it at Debug. One disconnect takes the count at
    /// most one past the limit.
    /// </summary>
    /// <returns>The change to raise, like <see cref="FindOrAdd"/>'s; null if nothing was forgotten.</returns>
    private SessionForgotten? ForgetPastLimit()
    {
        var disconnected = Disconnected();
        if (disconnected.Count <= limits.Disconnected.Value)
            return null;
        var oldest = disconnected.MinBy(s => s.DisconnectOrder)!;
        sessions.Remove(oldest.SessionId);
        hasForgotten = true;
        logger.LogDebug("Forgot disconnected Session {SessionId} to keep {Limit} disconnected Sessions.", oldest.SessionId, limits.Disconnected.Value);
        return new SessionForgotten(oldest.SessionId);
    }

    /// <summary>Every Session that is disconnected now.</summary>
    private List<KnownSession> Disconnected() => [.. sessions.Values.Where(s => s.IsDisconnected)];

    /// <summary>The Session <paramref name="connectionId"/> reports, if that connection is still the Session's current one.</summary>
    private KnownSession? ReportedBy(string connectionId) =>
        connections.TryGetValue(connectionId, out var sessionId)
        && sessions.TryGetValue(sessionId, out var session)
        && session.ConnectionId == connectionId
            ? session
            : null;

    private void WarnOlderSnapshot(Snapshot snapshot, long received) =>
        logger.LogWarning(
            "Session {SessionId} sent a Snapshot at Sequence {LastSequence}, older than the {Received} already received; recorded as a Resync.",
            snapshot.SessionId, snapshot.LastSequence, received);

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
}
