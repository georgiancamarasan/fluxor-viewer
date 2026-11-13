using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Middleware.Tests.CounterJson;

namespace FluxorViewer.Middleware.Tests;

// MW-7: Ignored Actions aren't reported, but the state they change is, with the next reported Action.
public class IgnoredActionTests
{
    [Fact]
    public async Task An_Ignored_Action_is_not_sent_and_takes_no_Sequence()
    {
        await using var harness = new Harness(configure: v => v.IgnoreAction(a => a is IncrementAction));
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new NothingAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("FluxorViewer.Middleware.Tests.NothingAction", action.ActionType);
        Assert.Equal(2, action.Sequence); // Straight after StoreInitializedAction.
    }

    [Fact]
    public async Task The_state_an_Ignored_Action_changed_goes_with_the_next_reported_Action()
    {
        await using var harness = new Harness(configure: v => v.IgnoreAction(a => a is IncrementAction));
        var (connection, _) = await harness.StartConnected();

        harness.DispatchIncrements(2); // Counter 0 → 2, unreported.
        harness.Dispatcher.Dispatch(new RenameAction("Grace"));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(["Counter", "Profile"], action.ChangedFeatures.Select(f => f.Name).Order());
        Assert.Equal(2, Count(action.ChangedFeatures));
    }

    [Fact]
    public async Task A_Snapshot_after_an_Ignored_Action_leaves_its_change_to_the_next_reported_Action()
    {
        await using var harness = new Harness(configure: v => v.IgnoreAction(a => a is IncrementAction));
        var (first, _) = await harness.StartConnected();
        harness.DispatchIncrements(2); // Counter 0 → 2, unreported.

        first.Close();
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);
        var second = harness.Viewer.Connections.Last();
        var resync = Assert.IsType<Snapshot>(await second.FirstMessage.WaitAsync(Wait.Timeout, Wait.CancellationToken));
        harness.Dispatcher.Dispatch(new RenameAction("Grace"));

        Assert.Equal(1, resync.LastSequence);
        Assert.Equal(0, resync.DroppedCount);
        Assert.Equal(0, Count(resync));
        var action = Assert.Single(await Harness.ActionsSent(second, 1));
        Assert.Equal(2, action.Sequence);
        Assert.Equal(2, Count(action.ChangedFeatures));
    }

    [Fact]
    public async Task An_Action_is_ignored_when_any_predicate_matches_it()
    {
        await using var harness = new Harness(configure: v => v
            .IgnoreAction(a => a is IncrementAction)
            .IgnoreAction(a => a is RenameAction));
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new RenameAction("Grace"));
        harness.Dispatcher.Dispatch(new NothingAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("FluxorViewer.Middleware.Tests.NothingAction", action.ActionType);
    }

    [Fact]
    public async Task A_predicate_that_throws_counts_as_false_and_is_logged_at_Warning_once_then_at_Debug()
    {
        await using var harness = new Harness(configure: v => v
            .IgnoreAction(a => a is IncrementAction or RenameAction ? throw new PredicateException() : false)
            .IgnoreAction(a => a is RenameAction));
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new RenameAction("Grace")); // Still ignored by the second predicate.
        harness.Dispatcher.Dispatch(new IncrementAction());

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Equal(["IncrementAction", "IncrementAction"], actions.Select(a => a.ActionType.Split('.').Last()));
        Assert.Equal([2L, 3L], actions.Select(a => a.Sequence));
        var logged = harness.Logs.Entries.Where(e => e.Exception is PredicateException).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug], logged.Select(e => e.Level));
        Assert.Contains("FluxorViewer.Middleware.Tests.IncrementAction", logged[0].Message);
    }

    private sealed class PredicateException : Exception;
}
