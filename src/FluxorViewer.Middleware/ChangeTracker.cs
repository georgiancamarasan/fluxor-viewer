using Fluxor;

namespace FluxorViewer.Middleware;

/// <summary>
/// The state reference last seen for each Feature, and the Sequence: finds an Action's Changed Features by comparing
/// references, since Fluxor states are immutable. Not thread-safe: Fluxor calls AfterDispatch one Action at a time.
/// </summary>
internal sealed class ChangeTracker
{
    private readonly IFeature[] features;
    private readonly Dictionary<IFeature, int> featureIndex;
    private readonly object?[] seen;
    private long lastSequence;

    /// <summary>Starts from the current state of every Feature in the store, before any Action.</summary>
    public ChangeTracker(IEnumerable<IFeature> features)
    {
        this.features = [.. features];
        featureIndex = this.features.Select((feature, index) => (feature, index)).ToDictionary(x => x.feature, x => x.index);
        seen = [.. this.features.Select(feature => feature.GetState())];
    }

    /// <summary>Every Feature with the state last seen for it.</summary>
    public IReadOnlyList<TrackedState> States => [.. features.Select((feature, index) => new TrackedState(feature, seen[index]))];

    /// <summary>
    /// Assigns <paramref name="action"/> the next Sequence and finds the Features whose state reference changed since the
    /// previous Action. The Sequence advances first and the new states count as seen last: if a Feature throws, the Viewer
    /// sees an honest gap, and the changes found so far go with the next Action.
    /// </summary>
    public PendingAction Track(object action, DateTimeOffset timestamp)
    {
        var sequence = ++lastSequence;
        var changed = FindChanged();
        foreach (var tracked in changed)
            seen[featureIndex[tracked.Feature]] = tracked.State;
        return new PendingAction(sequence, timestamp, action, changed);
    }

    private List<TrackedState> FindChanged()
    {
        List<TrackedState> changed = [];
        for (var index = 0; index < features.Length; index++)
        {
            var state = features[index].GetState();
            if (!ReferenceEquals(state, seen[index]))
                changed.Add(new TrackedState(features[index], state));
        }

        return changed;
    }
}
