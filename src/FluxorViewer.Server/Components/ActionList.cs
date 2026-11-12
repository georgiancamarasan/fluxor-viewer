using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>One row of the Action list.</summary>
/// <param name="Dimmed">The Action didn't change the selected Feature.</param>
public sealed record ActionRow(ActionEntry Entry, string ShortName, bool Dimmed);

/// <summary>
/// The rows one Viewer tab's Action list shows, which Action it has selected, and follow mode.
/// </summary>
/// <remarks>
/// The filter and the "changed only" toggle belong to the tab and survive switching Session. The selection is by
/// Sequence and kept while its row is hidden. While following, it's the newest visible row; selecting any other row
/// pauses follow mode, and losing the selection (switching Session, the Action leaving the History) resumes it.
/// </remarks>
public sealed class ActionList
{
    private Guid? sessionId;
    private IReadOnlyList<ActionEntry> actions = [];
    private string? feature;
    private readonly Dictionary<string, string> shortNames = [];

    /// <summary>Case-insensitive substring of the Action type's short name; blank shows every Action.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Hide Actions that didn't change the selected Feature. No effect without a selected Feature.</summary>
    public bool ChangedOnly { get; set; }

    /// <summary>Every Action in the selected Session, shown or not.</summary>
    public int ActionCount => actions.Count;

    public ActionRow[] Rows { get; private set; } = [];

    public ActionEntry? Selected { get; private set; }

    /// <summary>Follow mode: the newest visible row stays selected as Actions arrive.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Visible rows that arrived since follow mode paused; 0 while following.</summary>
    public int NewCount { get; private set; }

    // The newest Sequence in the History when follow mode paused.
    private long pausedAt;

    /// <summary>
    /// The developer clicked a row. The newest visible row turns follow mode on, any other pauses it. Sequences no
    /// longer in the History are ignored.
    /// </summary>
    public void Select(long sequence)
    {
        if (actions.FirstOrDefault(a => a.Sequence == sequence) is not { } action)
            return;
        Selected = action;
        Following = Rows is [.., var newest] && newest.Entry.Sequence == sequence;
        pausedAt = actions[^1].Sequence;
        Refilter();
    }

    /// <summary>Turns follow mode back on (the "▼ N new" pill), selecting the newest visible row.</summary>
    public void Resume()
    {
        Following = true;
        Refilter();
    }

    /// <summary>Clears the filter and the toggle.</summary>
    public void ShowAll()
    {
        Filter = "";
        ChangedOnly = false;
        Refilter();
    }

    /// <summary>Re-reads the selected Session (null when none) and Feature.</summary>
    public void Update(ReceivedSession? session, string? selectedFeature)
    {
        if (session?.SessionId != sessionId)
            Selected = null;
        sessionId = session?.SessionId;
        actions = session is null ? [] : [.. session.Actions];
        feature = selectedFeature;

        // Re-point the selection at the latest copy of its Action, or drop it if Clear or the cap removed it.
        Selected = Selected is { } selected ? actions.FirstOrDefault(a => a.Sequence == selected.Sequence) : null;
        if (Selected is null)
            Following = true;
        Refilter();
    }

    /// <summary>Applies a changed filter or toggle.</summary>
    public void Refilter()
    {
        var filter = Filter.Trim();
        var changedOnly = ChangedOnly && feature is not null;
        var rows = new List<ActionRow>(actions.Count);
        foreach (var action in actions)
        {
            var dimmed = feature is not null && !action.ChangedFeatures.Contains(feature);
            if (dimmed && changedOnly)
                continue;
            var name = ShortName(action.ActionType);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new ActionRow(action, name, dimmed));
        }
        Rows = [.. rows];
        if (Following)
            Selected = Rows is [.., var newest] ? newest.Entry : null;
        NewCount = Following ? 0 : Rows.Count(r => r.Entry.Sequence > pausedAt);
    }

    private string ShortName(string actionType)
    {
        if (!shortNames.TryGetValue(actionType, out var name))
            shortNames[actionType] = name = ActionTypeName.Short(actionType);
        return name;
    }
}
