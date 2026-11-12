using Fluxor;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware;

/// <summary>Turns tracked Feature states and pending Actions into protocol messages.</summary>
internal static class Messages
{
    public static Snapshot Snapshot(
        SessionInfo session, long lastSequence, long droppedCount, IEnumerable<TrackedState> features, ViewerJson json) => new(
        ProtocolVersion.Current,
        session.SessionId,
        session.AppName,
        session.Runtime,
        session.StartedAt,
        lastSequence,
        droppedCount,
        features.Select(f => FeatureState(f, json)).ToList());

    public static ActionRecorded ActionRecorded(SessionInfo session, PendingAction pending, ViewerJson json) => new(
        session.SessionId,
        pending.Sequence,
        pending.Timestamp,
        TypeNames.Readable(pending.Action.GetType()),
        json.Payload(pending.Action),
        pending.ChangedFeatures.Select(f => FeatureState(f, json)).ToList());

    private static FeatureState FeatureState(TrackedState tracked, ViewerJson json) => new(
        tracked.Feature.GetName(),
        TypeNames.Readable(tracked.Feature.GetStateType()),
        json.FeatureState(tracked.Feature.GetName(), tracked.State, tracked.Feature.GetStateType()));
}

/// <summary>Identity of a Session, fixed when its store initialises.</summary>
internal sealed record SessionInfo(Guid SessionId, string AppName, Runtime Runtime, DateTimeOffset StartedAt);

/// <summary>A Feature and the state reference last seen for it.</summary>
internal readonly record struct TrackedState(IFeature Feature, object? State);

/// <summary>An Action waiting to be serialised and sent.</summary>
internal sealed record PendingAction(long Sequence, DateTimeOffset Timestamp, object Action, IReadOnlyList<TrackedState> ChangedFeatures);
