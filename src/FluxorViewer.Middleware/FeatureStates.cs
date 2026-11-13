using Fluxor;

namespace FluxorViewer.Middleware;

/// <summary>
/// Every Feature's state as of some point in a Session, moved past each Action after it by that Action's Changed
/// Features. Not thread-safe: the <see cref="ActionBuffer"/> keeps its base in one under its lock.
/// </summary>
internal sealed class FeatureStates
{
    private readonly IReadOnlyDictionary<IFeature, int> positions;
    private readonly TrackedState[] states;

    /// <param name="initial">Every Feature's state at the starting point.</param>
    public FeatureStates(IReadOnlyList<TrackedState> initial)
        : this(initial.Select((tracked, position) => (tracked.Feature, position)).ToDictionary(x => x.Feature, x => x.position), [.. initial])
    {
    }

    private FeatureStates(IReadOnlyDictionary<IFeature, int> positions, TrackedState[] states)
    {
        this.positions = positions;
        this.states = states;
    }

    /// <summary>Moves on to just after <paramref name="action"/>: its Changed Features replace their states.</summary>
    public void MovePast(PendingAction action)
    {
        foreach (var changed in action.ChangedFeatures)
            states[positions[changed.Feature]] = changed;
    }

    /// <summary>A copy that moves on independently of this one.</summary>
    public FeatureStates Copy() => new(positions, [.. states]);

    /// <summary>Every Feature's state, in registration order, as it is now.</summary>
    public IReadOnlyList<TrackedState> ToList() => [.. states];
}
