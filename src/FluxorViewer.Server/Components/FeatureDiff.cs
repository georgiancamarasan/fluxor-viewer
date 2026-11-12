using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>What the Diff tab shows for one Feature at one Action, decided before any JSON reaches the browser.</summary>
public abstract record FeatureDiff
{
    private FeatureDiff() { }

    /// <summary>No Action is selected, so there's no before → after.</summary>
    public sealed record NoAction : FeatureDiff;

    /// <summary>The Feature isn't in the state after the Action.</summary>
    public sealed record Missing : FeatureDiff;

    /// <summary>The Feature isn't one of the Action's Changed Features.</summary>
    public sealed record NoChange : FeatureDiff;

    /// <summary>The Action replaced the Feature's state with one serialised to the same JSON.</summary>
    public sealed record Identical : FeatureDiff;

    /// <summary>The state on one side couldn't be serialised.</summary>
    /// <param name="Side">"before" or "after"; "after" when both failed.</param>
    public sealed record Error(string Side, string Message) : FeatureDiff;

    /// <summary>The JSON differs: the browser diffs it.</summary>
    /// <param name="Before">The state before the Action, or null when the Feature didn't exist yet (all of it is added).</param>
    public sealed record Changes(string? Before, string After) : FeatureDiff;

    /// <summary>Decides what the Diff tab shows for <paramref name="feature"/> at <paramref name="action"/>.</summary>
    public static FeatureDiff For(ActionEntry? action, string feature)
    {
        if (action is null)
            return new NoAction();
        if (action.After.GetValueOrDefault(feature) is not { } after)
            return new Missing();
        if (!action.ChangedFeatures.Contains(feature))
            return new NoChange();

        var before = action.Before.GetValueOrDefault(feature);
        if (SerialisationError.In(after.StateJson) is { } afterError)
            return new Error("after", afterError);
        if (before is not null && SerialisationError.In(before.StateJson) is { } beforeError)
            return new Error("before", beforeError);
        if (before is not null && string.Equals(before.StateJson, after.StateJson, StringComparison.Ordinal))
            return new Identical();
        return new Changes(before?.StateJson, after.StateJson);
    }
}
