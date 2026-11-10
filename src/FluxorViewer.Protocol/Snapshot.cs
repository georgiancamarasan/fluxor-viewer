namespace FluxorViewer.Protocol;

/// <summary>
/// The complete state of every Feature in a Session, sent first on every (re)connect.
/// </summary>
/// <param name="LastSequence">Sequence of the last Action dispatched before this Snapshot (0 if none).</param>
/// <param name="DroppedCount">Actions dropped by the middleware's buffer since the previous Snapshot.</param>
public sealed record Snapshot(
    ProtocolVersion ProtocolVersion,
    Guid SessionId,
    string AppName,
    Runtime Runtime,
    DateTimeOffset StartedAt,
    long LastSequence,
    long DroppedCount,
    IReadOnlyList<FeatureState> Features);
