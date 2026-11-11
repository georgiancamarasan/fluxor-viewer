using Fluxor;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware;

/// <summary>Turns tracked Feature states and pending Actions into protocol messages.</summary>
internal static class Messages
{
    public static Snapshot Snapshot(SessionInfo session, long lastSequence, long droppedCount, IEnumerable<TrackedState> features) => new(
        ProtocolVersion.Current,
        session.SessionId,
        session.AppName,
        session.Runtime,
        session.StartedAt,
        lastSequence,
        droppedCount,
        features.Select(FeatureState).ToList());

    public static ActionRecorded ActionRecorded(SessionInfo session, PendingAction pending) => new(
        session.SessionId,
        pending.Sequence,
        pending.Timestamp,
        TypeNames.Readable(pending.Action.GetType()),
        ViewerJson.Serialize(pending.Action, pending.Action.GetType()),
        pending.ChangedFeatures.Select(FeatureState).ToList());

    private static FeatureState FeatureState(TrackedState tracked) => new(
        tracked.Feature.GetName(),
        TypeNames.Readable(tracked.Feature.GetStateType()),
        ViewerJson.Serialize(tracked.State, tracked.Feature.GetStateType()));
}

/// <summary>Identity of a Session, fixed when its store initialises.</summary>
internal sealed record SessionInfo(Guid SessionId, string AppName, Runtime Runtime, DateTimeOffset StartedAt);

/// <summary>A Feature and the state reference last seen for it.</summary>
internal readonly record struct TrackedState(IFeature Feature, object? State);

/// <summary>An Action waiting to be serialised and sent.</summary>
internal sealed record PendingAction(long Sequence, DateTimeOffset Timestamp, object Action, IReadOnlyList<TrackedState> ChangedFeatures);
