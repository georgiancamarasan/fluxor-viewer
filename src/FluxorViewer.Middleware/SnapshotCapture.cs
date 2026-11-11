using Fluxor;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware;

/// <summary>Builds a Snapshot of every Feature in a store as it is right now.</summary>
internal static class SnapshotCapture
{
    public static Snapshot Capture(IStore store, SessionInfo session, long lastSequence, long droppedCount) => new(
        ProtocolVersion.Current,
        session.SessionId,
        session.AppName,
        session.Runtime,
        session.StartedAt,
        lastSequence,
        droppedCount,
        store.Features.Values
            .Select(f => new FeatureState(
                f.GetName(),
                f.GetStateType().FullName ?? f.GetStateType().Name,
                ViewerJson.Serialize(f.GetState(), f.GetStateType())))
            .ToList());
}

/// <summary>Identity of a Session, fixed when its store initialises.</summary>
internal sealed record SessionInfo(Guid SessionId, string AppName, Runtime Runtime, DateTimeOffset StartedAt);
