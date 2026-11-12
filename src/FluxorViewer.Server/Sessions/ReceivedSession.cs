using System.Collections.Immutable;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

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

    /// <summary>Whether the Viewer currently has a connection for the Session.</summary>
    public SessionStatus Status => ConnectionId is null ? SessionStatus.Disconnected : SessionStatus.Connected;

    /// <summary>Whether older entries were discarded to keep the History within its limit, since the last Clear.</summary>
    public bool HasDiscarded { get; init; }

    /// <summary>The Actions in the History, oldest first, without its Resyncs.</summary>
    public IEnumerable<ActionEntry> Actions => History.OfType<ActionEntry>();
}

/// <summary>Whether the Viewer currently has a connection for a Session. Disconnected Sessions may still reconnect.</summary>
public enum SessionStatus
{
    /// <summary>The Viewer has a connection for the Session.</summary>
    Connected,

    /// <summary>The Session's connection closed; it may have ended or may reconnect, and the Viewer can't tell which.</summary>
    Disconnected,
}
