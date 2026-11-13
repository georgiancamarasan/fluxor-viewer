using FluxorViewer.Server.Sessions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-8: the Viewer keeps at most its Disconnected Session limit of disconnected Sessions, forgetting the one that
// disconnected longest ago.
public class ForgettingTests
{
    private SessionRegistry registry = null!;
    private NamedSessions sessions = null!;

    public ForgettingTests() => UseLimit(2);

    private void UseLimit(int disconnected)
    {
        registry = NamedSessions.Registry(disconnected);
        sessions = new NamedSessions(registry);
    }

    [Fact]
    public void A_disconnect_past_the_limit_forgets_the_Session_that_disconnected_longest_ago()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("second", "first");

        sessions.Disconnect("third");

        Assert.Equal(["first", "third"], sessions.Kept());
    }

    [Fact]
    public void Connected_Sessions_do_not_count_and_are_never_forgotten()
    {
        sessions.Start("first", "second", "third", "connected");
        sessions.Disconnect("first", "second");

        sessions.Disconnect("third");

        Assert.Equal(["second", "third", "connected"], sessions.Kept());
    }

    [Fact]
    public void A_Session_that_reconnects_counts_from_its_latest_disconnect()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("first", "second");
        registry.SnapshotReceived("first again", NewSnapshot(sessions["first"]));
        sessions.Disconnect("first again");

        sessions.Disconnect("third");

        Assert.Equal(["first", "third"], sessions.Kept());
    }

    [Fact]
    public void With_a_limit_of_0_a_Session_is_forgotten_as_soon_as_it_disconnects()
    {
        UseLimit(0);
        sessions.Start("first", "second");

        sessions.Disconnect("first");

        Assert.Equal(["second"], sessions.Kept());
    }

    [Fact]
    public void A_forgotten_Session_that_reconnects_comes_back_as_a_new_Session_losing_only_its_dropped_Actions()
    {
        UseLimit(0);
        sessions.Start("first");
        var sessionId = sessions["first"];
        registry.ActionsReceived("first", Increments(sessionId, 1, 3));
        sessions.Disconnect("first");
        List<SessionChange> changes = [];
        registry.Changed += changes.Add;

        registry.SnapshotReceived("first again", NewSnapshot(sessionId) with { LastSequence = 5, DroppedCount = 1, Features = [Counter(5)] });

        Assert.Equal([new SessionAdded(sessionId), new EntriesAppended(sessionId, 1)], changes);
        var resync = Assert.IsType<ResyncEntry>(Assert.Single(registry.Find(sessionId)!.History));
        Assert.Equal(1, resync.Lost);
    }

    [Fact]
    public void Forgetting_is_raised_after_the_disconnect_that_caused_it()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("second", "first");
        List<SessionChange> changes = [];
        registry.Changed += changes.Add;

        sessions.Disconnect("third");

        Assert.Equal(
            [new SessionStatusChanged(sessions["third"], SessionStatus.Disconnected), new SessionForgotten(sessions["second"])],
            changes);
    }

    [Fact]
    public void Until_a_Session_is_forgotten_the_Viewer_has_none_to_tell_of()
    {
        sessions.Start("first", "second");

        sessions.Disconnect("first", "second");

        Assert.Null(registry.ForgottenBeyond);
    }

    [Fact]
    public void Once_a_Session_is_forgotten_the_Viewer_tells_of_it_with_its_limit_until_it_restarts_whatever_is_cleared()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("first", "second", "third");

        registry.Clear(sessions["second"]);

        Assert.Equal(new DisconnectedSessionLimit(2), registry.ForgottenBeyond);
    }
}
