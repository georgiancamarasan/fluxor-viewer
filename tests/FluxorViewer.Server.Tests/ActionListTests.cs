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

    private static ActionEntry Action(long sequence, string type = "App.IncrementAction", params string[] changed) => new(
        sequence, DateTimeOffset.UtcNow, type, "{}", changed.ToFrozenSet(),
        ImmutableDictionary<string, FeatureState>.Empty, ImmutableDictionary<string, FeatureState>.Empty);

    private static ReceivedSession Session(params ActionEntry[] history) => Session(Guid.NewGuid(), history);

    private static ReceivedSession Session(Guid id, params ActionEntry[] history) => new(
        id, "MyApp", Runtime.Server, DateTimeOffset.UtcNow, "c", history, ImmutableDictionary<string, FeatureState>.Empty, 0);

    private long[] Sequences => [.. list.Rows.Select(r => r.Entry.Sequence)];

    [Fact]
    public void Rows_are_the_Actions_oldest_first_with_short_type_names()
    {
        list.Update(Session(Action(1, "App.A"), Action(2, "App.Items.Loaded<App.Item>")), null);

        Assert.Equal([1, 2], Sequences);
        Assert.Equal(["A", "Loaded<Item>"], list.Rows.Select(r => r.ShortName));
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

        Assert.Equal([false, true, true], list.Rows.Select(r => r.Dimmed));
    }

    [Fact]
    public void Without_a_selected_Feature_nothing_is_dimmed_and_changed_only_has_no_effect()
    {
        list.ChangedOnly = true;

        list.Update(Session(Action(1, changed: "Counter"), Action(2)), null);

        Assert.Equal([1, 2], Sequences);
        Assert.All(list.Rows, r => Assert.False(r.Dimmed));
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
        list.Select(2);

        list.Filter = "A";
        list.Update(session, null);

        Assert.Equal([1], Sequences);
        Assert.Equal(2, list.Selected?.Sequence);
    }

    [Fact]
    public void Switching_Session_clears_the_selection_but_keeps_filter_and_toggle()
    {
        list.Update(Session(Action(1)), null);
        list.Select(1);
        list.Filter = "Incr";
        list.ChangedOnly = true;

        list.Update(Session(Action(1)), null);

        Assert.Null(list.Selected);
        Assert.Equal("Incr", list.Filter);
        Assert.True(list.ChangedOnly);
    }

    [Fact]
    public void An_Action_leaving_the_History_clears_the_selection()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1), Action(2)), null);
        list.Select(1);

        list.Update(Session(id, Action(2), Action(3)), null);

        Assert.Null(list.Selected);
    }

    [Fact]
    public void The_selection_follows_the_latest_copy_of_its_Action()
    {
        var id = Guid.NewGuid();
        list.Update(Session(id, Action(1)), null);
        list.Select(1);

        list.Update(Session(id, Action(1), Action(2)), null);

        Assert.Equal(1, list.Selected?.Sequence);
    }

    [Fact]
    public void Selecting_an_unknown_Sequence_is_ignored()
    {
        list.Update(Session(Action(1)), null);

        list.Select(9);

        Assert.Null(list.Selected);
    }
}
