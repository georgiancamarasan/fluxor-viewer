using FluxorViewer.Server.Sessions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// UI-12: Clear disconnected removes every disconnected Session at once and leaves connected Sessions as they are.
public class ClearDisconnectedTests
{
    private readonly SessionRegistry registry = NamedSessions.Registry(10);
    private readonly NamedSessions sessions;
    private readonly List<SessionChange> changes = [];

    public ClearDisconnectedTests()
    {
        sessions = new NamedSessions(registry);
        registry.Changed += changes.Add;
    }

    [Fact]
    public void Every_disconnected_Session_is_removed_and_connected_ones_are_kept()
    {
        sessions.Start("first", "connected", "second");
        sessions.Disconnect("first", "second");

        registry.ClearDisconnected();

        Assert.Equal(["connected"], sessions.Kept());
    }

    [Fact]
    public void A_connected_Session_keeps_its_History()
    {
        sessions.Start("connected", "gone");
        registry.ActionsReceived("connected", Increments(sessions["connected"], 1, 3));
        sessions.Disconnect("gone");

        registry.ClearDisconnected();

        Assert.Equal(3, registry.Find(sessions["connected"])!.History.Count);
    }

    [Fact]
    public void Each_removed_Session_raises_SessionCleared()
    {
        sessions.Start("first", "connected", "second");
        sessions.Disconnect("first", "second");
        changes.Clear();

        registry.ClearDisconnected();

        Assert.Equivalent(new[] { new SessionCleared(sessions["first"]), new SessionCleared(sessions["second"]) }, changes);
    }

    [Fact]
    public void With_no_disconnected_Sessions_nothing_changes_or_is_raised()
    {
        sessions.Start("connected");
        changes.Clear();

        registry.ClearDisconnected();

        Assert.Equal(["connected"], sessions.Kept());
        Assert.Empty(changes);
    }

    [Fact]
    public void The_Viewer_still_says_it_has_forgotten_Sessions()
    {
        var forgetting = NamedSessions.Registry(1);
        var named = new NamedSessions(forgetting);
        named.Start("forgotten", "disconnected");
        named.Disconnect("forgotten", "disconnected");

        forgetting.ClearDisconnected();

        Assert.Empty(named.Kept());
        Assert.Equal(new DisconnectedSessionLimit(1), forgetting.ForgottenBeyond);
    }
}
