using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>
/// Which Session one Viewer tab has selected, and the rules for selecting one on the developer's behalf.
/// </summary>
/// <remarks>
/// <see cref="Update"/> is given the full list each time rather than individual changes, so missed or reordered
/// <see cref="SessionChange"/>s can't leave the selection wrong.
/// </remarks>
public sealed class SessionSelection
{
    private HashSet<Guid> known = [];

    /// <summary>The Session to show; null while the developer has yet to pick among several.</summary>
    public Guid? Selected { get; private set; }

    /// <summary>The developer picked a Session.</summary>
    public void Select(Guid sessionId) => Selected = sessionId;

    /// <summary>Applies the selection rules to the current Sessions, given in the order the Viewer first saw them.</summary>
    public void Update(IReadOnlyList<ReceivedSession> sessions)
    {
        var current = sessions.FirstOrDefault(s => s.SessionId == Selected);

        // Follow a reloaded Host app: the selected Session disconnected and a new one from the same Host app and Runtime arrived.
        if (current is { Status: SessionStatus.Disconnected }
            && sessions.FirstOrDefault(s => !known.Contains(s.SessionId) && s.AppName == current.AppName && s.Runtime == current.Runtime) is { } successor)
            current = successor;

        if (current is null && sessions is [var only])
            current = only;

        Selected = current?.SessionId;
        known = [.. sessions.Select(s => s.SessionId)];
    }
}
