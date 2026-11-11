using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Sessions;

/// <summary>
/// What the ingest hub reports. The hub has already checked the protocol (version, connection binding, shape),
/// so implementations deal only with meaning.
/// </summary>
public interface ISessionRegistry
{
    /// <summary>A valid Snapshot arrived on <paramref name="connectionId"/>, which is now bound to its Session.</summary>
    void SnapshotReceived(string connectionId, Snapshot snapshot);

    /// <summary>Actions arrived for the Session bound to <paramref name="connectionId"/>.</summary>
    void ActionsReceived(string connectionId, IReadOnlyList<ActionRecorded> actions);

    /// <summary>The hub connection closed, cleanly or not.</summary>
    void ConnectionClosed(string connectionId);
}
