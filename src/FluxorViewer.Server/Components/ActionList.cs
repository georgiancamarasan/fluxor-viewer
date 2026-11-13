using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Components;

/// <summary>One row of the Action list: an <see cref="ActionRow"/> or a <see cref="ResyncRow"/>.</summary>
public abstract record ListRow
{
    private protected ListRow() { }

    /// <summary>The History entry the row shows.</summary>
    public abstract HistoryEntry Entry { get; }

    /// <summary>The time the row shows: when the Action was dispatched, or when the Viewer received the Resync.</summary>
    public abstract DateTimeOffset At { get; }
}

/// <summary>An Action's row.</summary>
/// <param name="Dimmed">The Action didn't change the selected Feature.</param>
public sealed record ActionRow(ActionEntry Action, string ShortName, bool Dimmed) : ListRow
{
    /// <inheritdoc/>
    public override HistoryEntry Entry => Action;

    /// <inheritdoc/>
    public override DateTimeOffset At => Action.Timestamp;
}

/// <summary>A Resync's row; the filter and "changed only" never hide it.</summary>
public sealed record ResyncRow(ResyncEntry Resync) : ListRow
{
    /// <inheritdoc/>
    public override HistoryEntry Entry => Resync;

    /// <inheritdoc/>
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
    private string filter = "";
    private bool changedOnly;

    /// <summary>
    /// Case-insensitive substring of the Action type's short name; blank shows every Action. Setting it re-filters the
    /// rows (and, while following, moves the selection).
    /// </summary>
    public string Filter
    {
        get => filter;
        set
        {
            filter = value;
            Refilter();
        }
    }

    /// <summary>
    /// Hide Actions that didn't change the selected Feature; no effect without a selected Feature. Setting it re-filters
    /// the rows (and, while following, moves the selection).
    /// </summary>
    public bool ChangedOnly
    {
        get => changedOnly;
        set
        {
            changedOnly = value;
            Refilter();
        }
    }

    /// <summary>Every Action in the selected Session, shown or not. Resyncs don't count.</summary>
    public int ActionCount { get; private set; }

    /// <summary>The Actions the filter and toggle leave visible; 0 means none match, though Resync rows may still show.</summary>
    public int VisibleActionCount { get; private set; }

    /// <summary>The rows shown, oldest first: visible Actions and every Resync.</summary>
    public ListRow[] Rows { get; private set; } = [];

    /// <summary>The selected Action or Resync, kept while its row is hidden; null when nothing is selected.</summary>
    public HistoryEntry? Selected { get; private set; }

    /// <summary>Follow mode: the newest visible row stays selected as entries arrive.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Visible rows that arrived since follow mode paused; 0 while following.</summary>
    public int NewCount { get; private set; }

    // The "N new" pill counts rows appended after this one.
    private long newestIdWhenPaused;

    /// <summary>
    /// The developer clicked the row of the entry with this Id. The newest visible row turns follow mode on, any other
    /// pauses it. Ids no longer in the History are ignored.
    /// </summary>
    public void Select(long id)
    {
        if (Find(id) is not { } entry)
            return;
        var wasFollowing = Following;
        Selected = entry;
        Following = Rows is [.., var newest] && newest.Entry.Id == id;
        if (wasFollowing && !Following)
            newestIdWhenPaused = entries[^1].Id;
        Refilter();
    }

    // UI-9: each key moves as clicking the row it lands on would, and returns whether the selection moved; a key with
    // nowhere to go does nothing. Ids grow in History order, so comparing them with the selection's finds the nearest
    // row whether or not the selection's own row is shown.

    /// <summary>↑: selects the nearest visible row before the selection in History order, as <see cref="Select"/> would.</summary>
    /// <returns>Whether there was one to select.</returns>
    public bool SelectPrevious() => SelectRow(Rows.LastOrDefault(row => row.Entry.Id < Selected?.Id));

    /// <summary>↓: selects the nearest visible row after the selection in History order, as <see cref="Select"/> would.</summary>
    /// <returns>Whether there was one to select.</returns>
    public bool SelectNext() => SelectRow(Rows.FirstOrDefault(row => row.Entry.Id > Selected?.Id));

    /// <summary>Home: selects the oldest visible row, as <see cref="Select"/> would.</summary>
    /// <returns>Whether the selection moved: false without rows or when it was already there.</returns>
    public bool SelectFirst() => SelectRow(Rows.FirstOrDefault());

    /// <summary>End: selects the newest visible row, as <see cref="Select"/> would, so follow mode resumes.</summary>
    /// <returns>Whether the selection moved: false without rows or when it was already there.</returns>
    public bool SelectLast() => SelectRow(Rows.LastOrDefault());

    private bool SelectRow(ListRow? row)
    {
        if (row is null || row.Entry.Id == Selected?.Id)
            return false;
        Select(row.Entry.Id);
        return true;
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
        (filter, changedOnly) = ("", false);
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

        // Re-point the selection at the latest copy of its entry, or drop it if Clear or the History limit discarded it.
        Selected = Selected is { } selected ? Find(selected.Id) : null;
        if (Selected is null)
            Following = true;
        Refilter();
    }

    /// <summary>Rebuilds the rows from the History, then keeps the selection on the newest row while following.</summary>
    private void Refilter()
    {
        Rows = [.. VisibleRows()];
        VisibleActionCount = Rows.Count(row => row is ActionRow);
        if (Following)
            Selected = Rows is [.., var newest] ? newest.Entry : null;
        NewCount = Following ? 0 : Rows.Count(row => row.Entry.Id > newestIdWhenPaused);
    }

    /// <summary>Every Resync, and each Action the filter and toggle leave visible, oldest first.</summary>
    private IEnumerable<ListRow> VisibleRows()
    {
        var text = filter.Trim();
        var hideUnchanged = changedOnly && feature is not null;
        foreach (var entry in entries)
        {
            if (entry is ResyncEntry resync)
                yield return new ResyncRow(resync);
            else if (VisibleRow((ActionEntry)entry, text, hideUnchanged) is { } row)
                yield return row;
        }
    }

    /// <summary>The Action's row, or null when the filter <paramref name="text"/> or "changed only" hides it.</summary>
    private ActionRow? VisibleRow(ActionEntry action, string text, bool hideUnchanged)
    {
        var dimmed = feature is not null && !action.ChangedFeatures.Contains(feature);
        var name = ShortName(action.ActionType);
        var hidden = (dimmed && hideUnchanged) || (text.Length > 0 && !name.Contains(text, StringComparison.OrdinalIgnoreCase));
        return hidden ? null : new ActionRow(action, name, dimmed);
    }

    private HistoryEntry? Find(long id) => entries.FirstOrDefault(e => e.Id == id);

    private string ShortName(string actionType)
    {
        if (!shortNames.TryGetValue(actionType, out var name))
            shortNames[actionType] = name = ActionTypeName.Short(actionType);
        return name;
    }
}
