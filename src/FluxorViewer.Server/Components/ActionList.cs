using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>One row of the Action list: an <see cref="ActionRow"/> or a <see cref="ResyncRow"/>.</summary>
public abstract record ListRow
{
    private protected ListRow() { }

    public abstract HistoryEntry Entry { get; }

    /// <summary>The time the row shows: when the Action was dispatched, or when the Viewer received the Resync.</summary>
    public abstract DateTimeOffset At { get; }
}

/// <summary>An Action's row.</summary>
/// <param name="Dimmed">The Action didn't change the selected Feature.</param>
public sealed record ActionRow(ActionEntry Action, string ShortName, bool Dimmed) : ListRow
{
    public override HistoryEntry Entry => Action;

    public override DateTimeOffset At => Action.Timestamp;
}

/// <summary>A Resync's row; the filter and "changed only" never hide it.</summary>
public sealed record ResyncRow(ResyncEntry Resync) : ListRow
{
    public override HistoryEntry Entry => Resync;

    public override DateTimeOffset At => Resync.ReceivedAt;

    /// <summary>"⚠ resync — N actions lost", or just "⚠ resync" when the Snapshot was older than what the Viewer had.</summary>
    public string Label => Resync.Lost switch
    {
        null => "⚠ resync",
        1 => "⚠ resync — 1 action lost",
        var lost => $"⚠ resync — {lost} actions lost",
    };

    /// <summary>Which Actions were lost, for the row's hover; null when the Viewer can't tell.</summary>
    public string? LostRange => Resync.FirstLost switch
    {
        null => null,
        var first when first == Resync.Sequence => $"Action #{first} was lost",
        var first => $"Actions #{first}–#{Resync.Sequence} were lost",
    };
}

/// <summary>
/// The rows one Viewer tab's Action list shows, which row it has selected, and follow mode.
/// </summary>
/// <remarks>
/// The filter and the "changed only" toggle belong to the tab and survive switching Session; they hide Actions only,
/// never Resyncs. The selection is by entry Id (a Sequence may appear more than once) and kept while its row is
/// hidden. While following, it's the newest visible row; selecting any other row pauses follow mode, and losing the
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

    /// <summary>The Actions the filter and toggle leave visible; 0 means none match, though Resync rows may still show.</summary>
    public int VisibleActionCount { get; private set; }

    public ListRow[] Rows { get; private set; } = [];

    public HistoryEntry? Selected { get; private set; }

    /// <summary>Follow mode: the newest visible row stays selected as entries arrive.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Visible rows that arrived since follow mode paused; 0 while following.</summary>
    public int NewCount { get; private set; }

    // The newest entry Id in the History when follow mode paused.
    private long pausedAt;

    /// <summary>
    /// The developer clicked the row of the entry with this Id. The newest visible row turns follow mode on, any other
    /// pauses it. Ids no longer in the History are ignored.
    /// </summary>
    public void Select(long id)
    {
        if (Find(id) is not { } entry)
            return;
        Selected = entry;
        Following = Rows is [.., var newest] && newest.Entry.Id == id;
        pausedAt = entries[^1].Id;
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
        Selected = Selected is { } selected ? Find(selected.Id) : null;
        if (Selected is null)
            Following = true;
        Refilter();
    }

    /// <summary>Applies a changed filter or toggle.</summary>
    public void Refilter()
    {
        var filter = Filter.Trim();
        var changedOnly = ChangedOnly && feature is not null;
        var rows = new List<ListRow>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is ResyncEntry resync)
            {
                rows.Add(new ResyncRow(resync));
                continue;
            }
            var action = (ActionEntry)entry;
            var dimmed = feature is not null && !action.ChangedFeatures.Contains(feature);
            if (dimmed && changedOnly)
                continue;
            var name = ShortName(action.ActionType);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(new ActionRow(action, name, dimmed));
        }
        Rows = [.. rows];
        VisibleActionCount = rows.Count(r => r is ActionRow);
        if (Following)
            Selected = Rows is [.., var newest] ? newest.Entry : null;
        NewCount = Following ? 0 : Rows.Count(r => r.Entry.Id > pausedAt);
    }

    private HistoryEntry? Find(long id) => entries.FirstOrDefault(e => e.Id == id);

    private string ShortName(string actionType)
    {
        if (!shortNames.TryGetValue(actionType, out var name))
            shortNames[actionType] = name = ActionTypeName.Short(actionType);
        return name;
    }
}
