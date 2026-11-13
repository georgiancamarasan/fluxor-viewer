using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-8: the Viewer keeps at most its Disconnected Session limit of disconnected Sessions, forgetting the one that
// disconnected longest ago.
public class ForgettingTests
{
    private SessionRegistry registry = WithLimit(2);

    private static SessionRegistry WithLimit(int disconnected) => new(
        NullLogger<SessionRegistry>.Instance, TimeProvider.System, SessionLimits.Default with { Disconnected = new DisconnectedSessionLimit(disconnected) });

    /// <summary>Starts a Session reported on a connection named after <paramref name="name"/>.</summary>
    private Guid Start(string name)
    {
        var sessionId = Guid.NewGuid();
        registry.SnapshotReceived(name, NewSnapshot(sessionId));
        return sessionId;
    }

    private void Disconnect(string name) => registry.ConnectionClosed(name);

    private Guid[] Kept() => [.. registry.GetSessions().Select(s => s.SessionId)];

    [Fact]
    public void A_disconnect_past_the_limit_forgets_the_Session_that_disconnected_longest_ago()
    {
        var a = Start("a");
        var b = Start("b");
        var c = Start("c");
        Disconnect("b");
        Disconnect("a");

        Disconnect("c");

        Assert.Equal([a, c], Kept());
    }

    [Fact]
    public void Connected_Sessions_do_not_count_and_are_never_forgotten()
    {
        var a = Start("a");
        var b = Start("b");
        var c = Start("c");
        var d = Start("d");

        Disconnect("a");
        Disconnect("b");

        Assert.Equal([a, b, c, d], Kept());
    }

    [Fact]
    public void A_Session_that_reconnects_counts_from_its_latest_disconnect()
    {
        var a = Start("a");
        var b = Start("b");
        var c = Start("c");
        Disconnect("a");
        Disconnect("b");
        registry.SnapshotReceived("a2", NewSnapshot(a));
        Disconnect("a2");

        Disconnect("c");

        Assert.Equal([a, c], Kept());
    }

    [Fact]
    public void With_a_limit_of_0_a_Session_is_forgotten_as_soon_as_it_disconnects()
    {
        registry = WithLimit(0);
        Start("a");
        var b = Start("b");

        Disconnect("a");

        Assert.Equal([b], Kept());
    }

    [Fact]
    public void A_forgotten_Session_that_reconnects_comes_back_as_a_new_Session_from_its_Snapshot()
    {
        registry = WithLimit(0);
        var a = Start("a");
        registry.ActionsReceived("a", Increments(a, 1, 3));
        Disconnect("a");

        registry.SnapshotReceived("a2", NewSnapshot(a) with { LastSequence = 5, Features = [Counter(5)] });

        var session = registry.Find(a)!;
        Assert.Equal(SessionStatus.Connected, session.Status);
        Assert.Empty(session.Actions);
        Assert.Equal("""{"Count":5}""", session.Current["Counter"].StateJson);
    }

    [Fact]
    public void Forgetting_is_raised_after_the_disconnect_that_caused_it()
    {
        Start("a");
        var b = Start("b");
        var c = Start("c");
        Disconnect("b");
        Disconnect("a");
        List<SessionChange> changes = [];
        registry.Changed += changes.Add;

        Disconnect("c");

        Assert.Equal([new SessionStatusChanged(c, SessionStatus.Disconnected), new SessionForgotten(b)], changes);
    }

    [Fact]
    public void The_Viewer_remembers_it_has_forgotten_until_it_restarts_whatever_is_cleared()
    {
        var a = Start("a");
        Start("b");
        Start("c");
        Disconnect("a");
        Disconnect("b");

        Disconnect("c");
        registry.Clear(a);

        Assert.True(registry.HasForgotten);
    }
}
