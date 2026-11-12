using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>One row of the Action list: an Action or a Resync.</summary>
/// <param name="ShortName">The Action type's short name; null for a Resync.</param>
/// <param name="Dimmed">The Action didn't change the selected Feature; never true for a Resync.</param>
public sealed record ActionRow(HistoryEntry Entry, string? ShortName, bool Dimmed);

/// <summary>
/// The rows one Viewer tab's Action list shows, which row it has selected, and follow mode.
/// </summary>
/// <remarks>
/// The filter and the "changed only" toggle belong to the tab and survive switching Session; they hide Actions only,
/// never Resyncs. The selection is by kind and Sequence (an Action and a Resync may share one) and kept while its row
/// is hidden. While following, it's the newest visible row; selecting any other row pauses follow mode, and losing the
/// selection (switching Session, the entry leaving the History) resumes it.
/// </remarks>
public sealed class ActionList
{
    private Guid? sessionId;
    private IReadOnlyList<HistoryEntry> entries = [];
    private string? feature;
    private readonly Dictionary<string, string> shortNames = [];

    /// <summary>Case-insensitive substring of the Action type's short name; blank shows every Action.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Hide Actions that didn't change the selected Feature. No effect without a selected Feature.</summary>
    public bool ChangedOnly { get; set; }

    /// <summary>Every Action in the selected Session, shown or not. Resyncs don't count.</summary>
    public int ActionCount { get; private set; }

    /// <summary>The visible Action rows; 0 means no Action matches, though Resync rows may still show.</summary>
    public int ActionRowCount { get; private set; }

    public ActionRow[] Rows { get; private set; } = [];

    public HistoryEntry? Selected { get; private set; }

    /// <summary>Follow mode: the newest visible row stays selected as entries arrive.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Visible rows that arrived since follow mode paused; 0 while following.</summary>
    public int NewCount { get; private set; }

    // The newest Sequence in the History when follow mode paused.
    private long pausedAt;

    /// <summary>
    /// The developer clicked an Action row. The newest visible row turns follow mode on, any other pauses it. Sequences
    /// no longer in the History are ignored.
    /// </summary>
    public void Select(long sequence) => Select(Find<ActionEntry>(sequence));

    /// <summary>The developer clicked a Resync row; otherwise as <see cref="Select(long)"/>.</summary>
    public void SelectResync(long sequence) => Select(Find<ResyncEntry>(sequence));

    private void Select(HistoryEntry? entry)
    {
        if (entry is null)
            return;
        Selected = entry;
        Following = Rows is [.., var newest] && ReferenceEquals(newest.Entry, entry);
        pausedAt = entries[^1].Sequence;
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
        entries = session?.History ?? [];
        ActionCount = entries.Count(e => e is ActionEntry);
        feature = selectedFeature;

        // Re-point the selection at the latest copy of its entry, or drop it if Clear or the cap removed it.
        Selected = Selected switch
        {
            ActionEntry a => Find<ActionEntry>(a.Sequence),
            ResyncEntry r => Find<ResyncEntry>(r.Sequence),
            _ => null,
        };
        if (Selected is null)
            Following = true;
        Refilter();
    }

    /// <summary>Applies a changed filter or toggle.</summary>
    public void Refilter()
    {
        var filter = Filter.Trim();
        var changedOnly = ChangedOnly && feature is not null;
        var rows = new List<ActionRow>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is not ActionEntry action)
            {
                rows.Add(new ActionRow(entry, null, false));
                continue;
            }
            var dimmed = feature is not null && !action.ChangedFeatures.Contains(feature);
            if (dimmed && changedOnly)
                continue;
            var name = ShortName(action.ActionType);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new ActionRow(action, name, dimmed));
        }
        Rows = [.. rows];
        ActionRowCount = rows.Count(r => r.Entry is ActionEntry);
        if (Following)
            Selected = Rows is [.., var newest] ? newest.Entry : null;
        NewCount = Following ? 0 : Rows.Count(r => r.Entry.Sequence > pausedAt);
    }

    private T? Find<T>(long sequence) where T : HistoryEntry =>
        entries.OfType<T>().FirstOrDefault(e => e.Sequence == sequence);

    private string ShortName(string actionType)
    {
        if (!shortNames.TryGetValue(actionType, out var name))
            shortNames[actionType] = name = ActionTypeName.Short(actionType);
        return name;
    }
}
