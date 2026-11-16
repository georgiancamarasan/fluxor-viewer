namespace FluxorViewer.Server.Components;

/// <summary>
/// Which Feature one Viewer tab has selected, and the Features it can choose from.
/// </summary>
/// <remarks>
/// The developer's last explicit pick for each Host app is remembered by name across that app's Sessions, whatever their
/// Runtime (UI-13). When the current Session doesn't have it, its first Feature is selected instead, without forgetting
/// the pick, so returning to a Session that has it selects it again. <see cref="Update"/> is given the full list each time, like <see cref="SessionSelection"/>.
/// </remarks>
public sealed class FeatureSelection
{
    private readonly Dictionary<string, string> picks = [];

    /// <summary>The selected Session's Feature names, alphabetically ignoring case.</summary>
    public IReadOnlyList<string> Features { get; private set; } = [];

    /// <summary>The Feature to show; null when the selected Session has no Features or none is selected.</summary>
    public string? Selected { get; private set; }

    /// <summary>The developer picked a Feature in a Session of the Host app named <paramref name="appName"/>.</summary>
    public void Select(string appName, string featureName)
    {
        picks[appName] = featureName;
        Selected = Features.Contains(featureName) ? featureName : Selected;
    }

    /// <summary>Takes up the picks stored in the browser, by app name. Call <see cref="Update"/> to apply them.</summary>
    public void Restore(IReadOnlyDictionary<string, string> storedPicks)
    {
        foreach (var (appName, featureName) in storedPicks)
            picks[appName] = featureName;
    }

    /// <summary>
    /// Applies the selection rules to the selected Session's Host app and Features (null and none when no Session is
    /// selected).
    /// </summary>
    public void Update(string? appName, IEnumerable<string> featureNames)
    {
        var picked = appName is null ? null : picks.GetValueOrDefault(appName);
        Features = [.. featureNames.Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)];
        Selected = picked is not null && Features.Contains(picked) ? picked : Features.FirstOrDefault();
    }
}
