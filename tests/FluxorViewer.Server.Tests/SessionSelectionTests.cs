using System.Collections.Immutable;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components;
using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.Tests;

// UI-1: which Session the picker selects on its own.
public class SessionSelectionTests
{
    private readonly SessionSelection selection = new();

    private static ReceivedSession Session(string appName = "MyApp", Runtime runtime = Runtime.WebAssembly) => new(
        Guid.NewGuid(), appName, runtime, DateTimeOffset.UtcNow, "c", [], ImmutableDictionary<string, FeatureState>.Empty, 0);

    private static ReceivedSession Disconnected(ReceivedSession session) => session with { ConnectionId = null };

    [Fact]
    public void Nothing_is_selected_while_there_are_no_Sessions()
    {
        selection.Update([]);

        Assert.Null(selection.Selected);
    }

    [Fact]
    public void A_single_Session_is_selected()
    {
        var only = Session();

        selection.Update([only]);

        Assert.Equal(only.SessionId, selection.Selected);
    }

    [Fact]
    public void With_several_Sessions_nothing_is_selected_until_the_developer_picks()
    {
        var a = Session();
        var b = Session();

        selection.Update([a, b]);
        Assert.Null(selection.Selected);

        selection.Select(b.SessionId);
        selection.Update([a, b]);
        Assert.Equal(b.SessionId, selection.Selected);
    }

    [Fact]
    public void A_second_Session_arriving_keeps_the_selection()
    {
        var first = Session();
        selection.Update([first]);

        selection.Update([first, Session(appName: "Other")]);

        Assert.Equal(first.SessionId, selection.Selected);
    }

    [Fact]
    public void When_the_selected_Session_is_removed_the_selection_empties_and_a_single_remaining_Session_is_selected()
    {
        var a = Session();
        var b = Session(appName: "Other");
        selection.Update([a, b]);
        selection.Select(a.SessionId);
        selection.Update([a, b]);

        selection.Update([b]);

        Assert.Equal(b.SessionId, selection.Selected);
    }

    [Fact]
    public void When_the_selected_Session_is_removed_among_several_nothing_is_selected()
    {
        var a = Session();
        var b = Session(appName: "B");
        var c = Session(appName: "C");
        selection.Update([a, b, c]);
        selection.Select(a.SessionId);

        selection.Update([b, c]);

        Assert.Null(selection.Selected);
    }

    [Fact]
    public void A_reloaded_app_is_followed_when_the_selected_Session_has_disconnected()
    {
        var old = Session();
        selection.Update([old]);

        var reloaded = Session();
        selection.Update([Disconnected(old), reloaded]);

        Assert.Equal(reloaded.SessionId, selection.Selected);
    }

    [Fact]
    public void A_connected_selected_Session_is_not_left_for_a_new_one_from_the_same_app()
    {
        var current = Session();
        selection.Update([current]);

        selection.Update([current, Session()]);

        Assert.Equal(current.SessionId, selection.Selected);
    }

    [Fact]
    public void A_new_Session_from_another_app_or_Runtime_is_not_followed()
    {
        var old = Session();
        selection.Update([old]);

        selection.Update([Disconnected(old), Session(appName: "Other"), Session(runtime: Runtime.Server)]);

        Assert.Equal(old.SessionId, selection.Selected);
    }

    [Fact]
    public void After_an_InteractiveAuto_reload_the_WebAssembly_Session_is_followed_once_it_arrives()
    {
        var wasm = Session();
        selection.Update([wasm]);

        var server = Session(runtime: Runtime.Server);
        selection.Update([Disconnected(wasm), server]);
        Assert.Equal(wasm.SessionId, selection.Selected);

        var newWasm = Session();
        selection.Update([Disconnected(wasm), server, newWasm]);
        Assert.Equal(newWasm.SessionId, selection.Selected);
    }

    [Fact]
    public void When_two_reloaded_tabs_arrive_together_the_first_to_arrive_is_followed()
    {
        var old = Session();
        selection.Update([old]);

        var first = Session();
        var second = Session();
        selection.Update([Disconnected(old), first, second]);

        Assert.Equal(first.SessionId, selection.Selected);
    }

    [Fact]
    public void A_Session_already_known_is_not_followed_later()
    {
        // The developer picked an old, disconnected Session while a newer one from the same app exists.
        var old = Disconnected(Session());
        var newer = Session();
        selection.Update([old, newer]);
        selection.Select(old.SessionId);

        selection.Update([old, newer]);

        Assert.Equal(old.SessionId, selection.Selected);
    }
}
