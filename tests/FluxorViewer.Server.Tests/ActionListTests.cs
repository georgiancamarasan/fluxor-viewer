using System.Collections.Frozen;
using System.Collections.Immutable;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components;
using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Tests;

// UI-3: which Action rows the list shows, how, and which one is selected.
public class ActionListTests
{
    private readonly ActionList list = new();

    // An entry's Id defaults to its Sequence; tests that need them apart give it explicitly.
    private static ActionEntry Action(long sequence, string type = "App.IncrementAction", long? id = null, params string[] changed) => new(
        id ?? sequence, sequence, DateTimeOffset.UtcNow, type, "{}", changed.ToFrozenSet(),
        ImmutableDictionary<string, FeatureState>.Empty, ImmutableDictionary<string, FeatureState>.Empty);

    private static ResyncEntry Resync(long sequence, long? lost = 1, long? id = null) => new(
        id ?? sequence, sequence, DateTimeOffset.UtcNow, lost, null, ImmutableDictionary<string, FeatureState>.Empty);

    private static ReceivedSession Session(params HistoryEntry[] history) => Session(Guid.NewGuid(), history);

    private static ReceivedSession Session(Guid id, params HistoryEntry[] history) => new(
        id, "MyApp", Runtime.Server, DateTimeOffset.UtcNow, "c", history, ImmutableDictionary<string, FeatureState>.Empty, 0);

    private long[] Sequences => [.. list.Rows.Select(r => r.Entry.Sequence)];

    [Fact]
    public void Rows_are_the_Actions_oldest_first_with_short_type_names()
    {
        list.Update(Session(Action(1, "App.A"), Action(2, "App.Items.Loaded<App.Item>")), null);

        Assert.Equal([1, 2], Sequences);
        Assert.Equal(["A", "Loaded<Item>"], list.Rows.Cast<ActionRow>().Select(r => r.ShortName));
        Assert.Equal(2, list.ActionCount);
    }

    [Fact]
    public void No_Session_means_no_rows()
    {
        list.Update(null, null);

        Assert.Empty(list.Rows);
        Assert.Equal(0, list.ActionCount);
    }

    [Fact]
    public void Actions_that_did_not_change_the_selected_Feature_are_dimmed()
    {
        list.Update(Session(Action(1, changed: "Counter"), Action(2, changed: "Auth"), Action(3)), "Counter");

        Assert.Equal([false, true, true], list.Rows.Cast<ActionRow>().Select(r => r.Dimmed));
    }

    [Fact]
    public void Without_a_selected_Feature_nothing_is_dimmed_and_changed_only_has_no_effect()
    {
        list.ChangedOnly = true;

        list.Update(Session(Action(1, changed: "Counter"), Action(2)), null);

        Assert.Equal([1, 2], Sequences);
        Assert.All(list.Rows.Cast<ActionRow>(), r => Assert.False(r.Dimmed));
    }

    [Fact]
    public void Changed_only_hides_Actions_that_did_not_change_the_selected_Feature()
    {
        list.ChangedOnly = true;

        list.Update(Session(Action(1, changed: "Counter"), Action(2, changed: "Auth"), Action(3, changed: "Counter")), "Counter");

        Assert.Equal([1, 3], Sequences);
    }

    [Fact]
    public void The_filter_matches_short_names_case_insensitively()
    {
        list.Filter = "  incr ";

        list.Update(Session(Action(1, "App.IncrementAction"), Action(2, "App.Increments.FetchAction"), Action(3, "App.DecrementAction")), null);

        Assert.Equal([1], Sequences);
    }

    [Fact]
    public void Show_all_clears_the_filter_and_the_toggle()
    {
        list.Filter = "nothing";
        list.ChangedOnly = true;

        list.ShowAll();

        Assert.Equal("", list.Filter);
        Assert.False(list.ChangedOnly);
    }

    [Fact]
    public void A_selected_Action_stays_selected_while_hidden()
    {
        var session = Session(Action(1, "App.A"), Action(2, "App.B"));
        list.Update(session, null);
        list.Select(1);

        list.Filter = "B";
        list.Update(session, null);

        Assert.Equal([2], Sequences);
        Assert.Equal(1, list.Selected?.Sequence);
    }

    [Fact]
    public void Switching_Session_follows_its_newest_Action_and_keeps_filter_and_toggle()
    {
        list.Update(Session(Action(1), Action(2)), null);
        list.Select(1);
        list.Filter = "Incr";
        list.ChangedOnly = true;

        list.Update(Session(Action(7)), null);

        Assert.True(list.Following);
        Assert.Equal(7, list.Selected?.Sequence);
        Assert.Equal("Incr", list.Filter);
        Assert.True(list.ChangedOnly);
    }

    [Fact]
    public void An_Action_leaving_the_History_resumes_follow_mode()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);

        list.Update(Session(id, Action(2), Action(3)), null);

        Assert.True(list.Following);
        Assert.Equal(3, list.Selected?.Sequence);
    }

    [Fact]
    public void The_selection_follows_the_latest_copy_of_its_Action()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);

        list.Update(Session(id, Action(1), Action(2), Action(3)), null);

        Assert.Equal(1, list.Selected?.Sequence);
    }

    // UI-7: follow mode.

    [Fact]
    public void Follow_mode_is_on_by_default_and_selects_the_newest_Action()
    {
        list.Update(Session(Action(1), Action(2)), null);

        Assert.True(list.Following);
        Assert.Equal(2, list.Selected?.Sequence);
    }

    [Fact]
    public void Follow_mode_selects_the_newest_visible_row_or_nothing_when_none_is_visible()
    {
        var session = Session(Action(1, "App.A"), Action(2, "App.B"));

        list.Filter = "A";
        list.Update(session, null);
        Assert.Equal(1, list.Selected?.Sequence);

        list.Filter = "nothing";
        Assert.Null(list.Selected);
        Assert.True(list.Following);
    }

    [Fact]
    public void Selecting_the_newest_visible_row_keeps_follow_mode_on()
    {
        list.Update(Session(Action(1), Action(2)), null);

        list.Select(2);

        Assert.True(list.Following);
    }

    [Fact]
    public void While_paused_new_Actions_are_counted_instead_of_selected()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);
        Assert.False(list.Following);
        Assert.Equal(0, list.NewCount);

        list.Update(Session(id, Action(1), Action(2), Action(3), Action(4)), null);

        Assert.Equal(1, list.Selected?.Sequence);
        Assert.Equal(2, list.NewCount);
    }

    [Fact]
    public void Selecting_another_older_row_while_paused_keeps_counting_from_when_it_paused()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2), Action(3)), null);
        list.Select(2);
        list.Update(Session(id, Action(1), Action(2), Action(3), Action(4), Action(5)), null);

        list.Select(1);

        Assert.Equal(2, list.NewCount);
    }

    [Fact]
    public void Only_visible_new_Actions_are_counted()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1, "App.A"), Action(2, "App.A")), null);
        list.Select(1);
        list.Update(Session(id, Action(1, "App.A"), Action(2, "App.A"), Action(3, "App.A"), Action(4, "App.B")), null);

        list.Filter = "A";

        Assert.Equal(1, list.NewCount);
    }

    [Fact]
    public void Resuming_selects_the_newest_visible_row_and_clears_the_count()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);
        list.Update(Session(id, Action(1), Action(2), Action(3)), null);

        list.Resume();

        Assert.True(list.Following);
        Assert.Equal(3, list.Selected?.Sequence);
        Assert.Equal(0, list.NewCount);
    }

    [Fact]
    public void Selecting_an_unknown_Sequence_is_ignored()
    {
        list.Update(Session(Action(1), Action(2)), null);
        list.Select(1);

        list.Select(9);

        Assert.Equal(1, list.Selected?.Sequence);
        Assert.False(list.Following);
    }

    // IN-5: Resync rows.

    [Fact]
    public void Resyncs_are_rows_in_History_order_that_the_filter_and_changed_only_never_hide()
    {
        list.Filter = "nothing matches";
        list.ChangedOnly = true;

        list.Update(Session(Action(1, changed: "Counter"), Resync(5, id: 2), Action(6, id: 3, changed: "Counter")), "Counter");

        Assert.IsType<ResyncRow>(Assert.Single(list.Rows));
        Assert.Equal(2, list.ActionCount);
        Assert.Equal(0, list.VisibleActionCount); // so the pane still says "No Actions match"
    }

    [Fact]
    public void Resyncs_do_not_count_as_Actions()
    {
        list.Update(Session(Resync(40, lost: 12, id: 1)), null);

        Assert.Equal(0, list.ActionCount);
        Assert.IsType<ResyncRow>(Assert.Single(list.Rows));
    }

    [Fact]
    public void Entries_sharing_a_Sequence_are_selected_separately()
    {
        // After an older Snapshot, Actions 2 and 3 arrive again.
        list.Update(Session(Action(1), Action(2), Action(3), Resync(1, lost: null, id: 4), Action(2, id: 5), Action(3, id: 6)), null);

        list.Select(4);
        Assert.IsType<ResyncEntry>(list.Selected);

        list.Select(5);
        Assert.Equal((5, 2), (list.Selected!.Id, list.Selected.Sequence));

        list.Select(2);
        Assert.Equal((2, 2), (list.Selected!.Id, list.Selected.Sequence));
    }

    [Fact]
    public void A_selected_Resync_stays_selected_as_the_History_is_re_read()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Resync(5, id: 2), Action(6, id: 3)), null);
        list.Select(2);

        list.Update(Session(id, Action(1), Resync(5, id: 2), Action(6, id: 3), Action(7, id: 4)), null);

        Assert.IsType<ResyncEntry>(list.Selected);
        Assert.False(list.Following);
    }

    [Fact]
    public void Follow_mode_selects_a_Resync_that_is_newest()
    {
        list.Update(Session(Action(1), Resync(5, id: 2)), null);

        Assert.True(list.Following);
        Assert.IsType<ResyncEntry>(list.Selected);
    }

    [Fact]
    public void While_paused_new_Resyncs_are_counted_with_new_Actions()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);

        list.Update(Session(id, Action(1), Action(2), Resync(5, id: 3), Action(6, id: 4)), null);

        Assert.Equal(2, list.NewCount);
    }

    [Fact]
    public void While_paused_what_follows_an_older_Snapshot_is_counted_though_its_Sequences_are_lower()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2), Action(3)), null);
        list.Select(1);

        list.Update(Session(id, Action(1), Action(2), Action(3), Resync(1, lost: null, id: 4), Action(2, id: 5)), null);

        Assert.Equal(2, list.NewCount);
    }
}
