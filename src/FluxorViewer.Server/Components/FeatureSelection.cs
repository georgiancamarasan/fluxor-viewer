namespace FluxorViewer.Server.Components;

/// <summary>
/// Which Feature one Viewer tab has selected, and the Features it can choose from.
/// </summary>
/// <remarks>
/// The developer's last explicit pick is remembered by name across Sessions. When the current Session doesn't have it,
/// its first Feature is selected instead, without forgetting the pick, so returning to a Session that has it selects
/// it again. <see cref="Update"/> is given the full list each time, like <see cref="SessionSelection"/>.
/// </remarks>
public sealed class FeatureSelection
{
    private string? picked;

    /// <summary>The selected Session's Feature names, alphabetically ignoring case.</summary>
    public IReadOnlyList<string> Features { get; private set; } = [];

    public string? Selected { get; private set; }

    /// <summary>The developer picked a Feature.</summary>
    public void Select(string featureName)
    {
        picked = featureName;
        Selected = Features.Contains(featureName) ? featureName : Selected;
    }

    /// <summary>Applies the selection rules to the selected Session's Features (none when no Session is selected).</summary>
    public void Update(IEnumerable<string> featureNames)
    {
        Features = [.. featureNames.Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)];
        Selected = picked is not null && Features.Contains(picked) ? picked : Features.FirstOrDefault();
    }
}
