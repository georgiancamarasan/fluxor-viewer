using System.Collections.Concurrent;
using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Tests;

/// <summary>
/// Records the changes a registry raises for one Session, in order, until disposed. A shared Viewer serves other tests'
/// Sessions too, so their changes are left out.
/// </summary>
internal sealed class ChangeRecorder : IDisposable
{
    private readonly SessionRegistry registry;
    private readonly Guid sessionId;
    private readonly ConcurrentQueue<SessionChange> changes = new();

    /// <summary>Starts recording <paramref name="sessionId"/>'s changes from <paramref name="registry"/>.</summary>
    public ChangeRecorder(SessionRegistry registry, Guid sessionId)
    {
        this.registry = registry;
        this.sessionId = sessionId;
        registry.Changed += Record;
    }

    /// <summary>The changes so far, oldest first.</summary>
    public IReadOnlyList<SessionChange> Changes => [.. changes];

    private void Record(SessionChange change)
    {
        if (change.SessionId == sessionId)
            changes.Enqueue(change);
    }

    /// <summary>Stops recording.</summary>
    public void Dispose() => registry.Changed -= Record;
}
