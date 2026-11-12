namespace FluxorViewer.Protocol;

/// <summary>One dispatched Action and the Features whose state it changed.</summary>
/// <param name="Sequence">Per-Session, strictly increasing, starting at 1.</param>
/// <param name="ActionType">Full name of the Action's type.</param>
/// <param name="Payload">The Action as JSON; opaque to the transport.</param>
/// <param name="ChangedFeatures">Only the Features whose state reference changed.</param>
public sealed record ActionRecorded(
    Guid SessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    string ActionType,
    string Payload,
    IReadOnlyList<FeatureState> ChangedFeatures);
