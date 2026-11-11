namespace FluxorViewer.Server.Sessions;

/// <summary>
/// Something changed about a Session in the <see cref="SessionRegistry"/>. A hint to re-read it, not a log to replay:
/// changes from concurrent callers may arrive out of order.
/// </summary>
public abstract record SessionChange(Guid SessionId);

/// <summary>The Viewer saw this Session for the first time; it's connected.</summary>
public sealed record SessionAdded(Guid SessionId) : SessionChange(SessionId);

/// <summary>The Session connected or disconnected.</summary>
public sealed record SessionStatusChanged(Guid SessionId, SessionStatus Status) : SessionChange(SessionId);

/// <summary>Entries were appended to the Session's History.</summary>
/// <param name="Count">How many entries this call appended. Informational: don't compute positions from it.</param>
public sealed record EntriesAppended(Guid SessionId, int Count) : SessionChange(SessionId);

/// <summary>The Session was cleared. If it was disconnected it's gone; otherwise its History is empty and it goes on recording.</summary>
public sealed record SessionCleared(Guid SessionId) : SessionChange(SessionId);
