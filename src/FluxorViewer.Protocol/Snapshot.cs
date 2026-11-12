namespace FluxorViewer.Protocol;

/// <summary>
/// The complete state of every Feature in a Session, sent first on every (re)connect.
/// </summary>
/// <param name="LastSequence">
/// Sequence of the last Action whose state the Snapshot includes: the last one that left the middleware's buffer, sent or
/// dropped (0 if none). Never less than any Sequence already sent (ADR 0005).
/// </param>
/// <param name="DroppedCount">Dropped Actions since the last Snapshot the Viewer accepted.</param>
public sealed record Snapshot(
    ProtocolVersion ProtocolVersion,
    Guid SessionId,
    string AppName,
    Runtime Runtime,
    DateTimeOffset StartedAt,
    long LastSequence,
    long DroppedCount,
    IReadOnlyList<FeatureState> Features);
