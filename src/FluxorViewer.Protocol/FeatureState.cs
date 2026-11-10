namespace FluxorViewer.Protocol;

/// <summary>The state of one Feature, serialised by the middleware.</summary>
/// <param name="Name">The Feature's name as registered with Fluxor.</param>
/// <param name="StateType">Full name of the Feature's state type.</param>
/// <param name="StateJson">The state as JSON; opaque to the transport.</param>
public sealed record FeatureState(string Name, string StateType, string StateJson);
