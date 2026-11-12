namespace FluxorViewer.Server.Components;

/// <summary>
/// The developer's expand/collapse choices for JSON trees, kept for the Viewer tab per key: a Feature name for the State
/// tab, a full Action type for the payload strip.
/// </summary>
public sealed class TreeChoices
{
    private readonly Dictionary<string, Dictionary<string, bool>> byKey = [];

    /// <summary>
    /// The choices for <paramref name="key"/> by JSON path, created empty on first use so later toggles are kept; a
    /// throwaway set when <paramref name="key"/> is null.
    /// </summary>
    public Dictionary<string, bool> For(string? key)
    {
        if (key is null)
            return [];
        if (!byKey.TryGetValue(key, out var choices))
            byKey[key] = choices = [];
        return choices;
    }
}
