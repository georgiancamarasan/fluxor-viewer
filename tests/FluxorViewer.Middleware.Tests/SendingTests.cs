using System.Diagnostics;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-5: sending is batched, bounded, and costs dispatch almost nothing.
public class SendingTests
{
    private static CancellationToken Ct => Wait.Ct;

    private static TaskCompletionSource Stalled() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Dispatches one Action and waits until the sender is blocked sending it, so everything dispatched next stays
    /// queued until the returned gate is released.
    /// </summary>
    private static async Task<TaskCompletionSource> StallSender(Harness harness, FakeViewer.FakeConnection connection)
    {
        var gate = harness.Viewer.ActionsGate = Stalled();
        var started = connection.ActionSendsStarted;
        harness.Dispatcher.Dispatch(new IncrementAction());
        await Wait.Until(() => connection.ActionSendsStarted > started);
        return gate;
    }

    [Fact]
    public async Task Queued_Actions_are_sent_in_order_in_batches_of_at_most_100()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var gate = await StallSender(harness, connection); // Sequence 2 in flight.

        for (var i = 0; i < 249; i++)
            harness.Dispatcher.Dispatch(new IncrementAction());
        gate.SetResult();
        var actions = await Harness.ActionsSent(connection, 250);

        Assert.Equal(Enumerable.Range(2, 250).Select(i => (long)i), actions.Select(a => a.Sequence));
        Assert.Equal([1, 100, 100, 49], connection.Batches.Select(b => b.Count));
    }

    [Fact]
    public async Task Overflow_is_followed_by_a_Snapshot_with_the_dropped_count()
    {
        await using var harness = new Harness(bufferSize: 10);
        var (connection, first) = await harness.StartConnected();
        var gate = await StallSender(harness, connection); // Sequence 2 in flight.

        for (var i = 0; i < 49; i++) // 3..12 fill the buffer; 13..51 are dropped.
            harness.Dispatcher.Dispatch(new IncrementAction());
        gate.SetResult();
        await Wait.Until(() => connection.Sent.OfType<Snapshot>().Count() == 2);
        harness.Dispatcher.Dispatch(new IncrementAction());
        await Wait.Until(() => connection.Sent.LastOrDefault() is ActionRecorded { Sequence: 52 });

        // Sent: Snapshot, Actions 2..12, Snapshot (LastSequence 51, 39 dropped), Action 52.
        var sent = connection.Sent.ToList();
        var resync = Assert.IsType<Snapshot>(sent.Single(m => m is Snapshot && !ReferenceEquals(m, first)));
        var before = sent.TakeWhile(m => !ReferenceEquals(m, resync)).OfType<ActionRecorded>().Select(a => a.Sequence).ToList();
        var after = sent.SkipWhile(m => !ReferenceEquals(m, resync)).OfType<ActionRecorded>().Select(a => a.Sequence).ToList();

        Assert.Same(first, sent[0]);
        Assert.Equal(Enumerable.Range(2, 11).Select(i => (long)i), before);
        Assert.Equal(51, resync.LastSequence);
        Assert.Equal(39, resync.DroppedCount);
        Assert.Equal(50, Count(resync));
        Assert.Equal([52L], after);
        Assert.Equal(first.SessionId, resync.SessionId);
    }

    [Fact]
    public async Task The_drop_count_resets_after_each_Snapshot()
    {
        await using var harness = new Harness(bufferSize: 5);
        var (connection, _) = await harness.StartConnected();

        for (var round = 1; round <= 2; round++)
        {
            // Each round: 1 in flight, 5 queued, 14 dropped.
            var gate = await StallSender(harness, connection);
            for (var i = 0; i < 19; i++)
                harness.Dispatcher.Dispatch(new IncrementAction());
            gate.SetResult();
            var expected = round + 1;
            await Wait.Until(() => connection.Sent.OfType<Snapshot>().Count() == expected);
        }

        var snapshots = connection.Sent.OfType<Snapshot>().ToList();
        Assert.Equal([0L, 14L, 14L], snapshots.Select(s => s.DroppedCount));
        Assert.Equal([1L, 21L, 41L], snapshots.Select(s => s.LastSequence));
        Assert.Equal(12, connection.Actions.Count);
    }

    [Fact]
    public async Task An_Action_that_fails_to_serialise_is_skipped_within_its_batch()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var gate = harness.Viewer.ActionsGate = Stalled();

        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new ExplodingAction());
        harness.Dispatcher.Dispatch(new IncrementAction());
        gate.SetResult();
        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Level == LogLevel.Warning));
        var actions = await Harness.ActionsSent(connection, 3);

        Assert.Equal([2L, 3L, 5L], actions.Select(a => a.Sequence));
    }

    [Fact]
    public async Task Dispatching_5000_Actions_costs_little_more_than_with_the_middleware_disabled()
    {
        const int actions = 5_000;

        await using var disabled = new Harness(enabled: false);
        await disabled.InitializeAsync();

        await using var unreachable = new Harness(viewer: new FakeViewer { ConnectFailure = new HttpRequestException("Refused") });
        await unreachable.InitializeAsync();
        await Wait.Until(() => unreachable.Logs.Entries.Any(e => e.Exception is HttpRequestException));

        await using var stalled = new Harness();
        await stalled.StartConnected();
        stalled.Viewer.ActionsGate = Stalled();

        // Warm up the dispatch path (JIT) for each before timing.
        foreach (var harness in new[] { disabled, unreachable, stalled })
            Time(harness.Dispatcher, 200);

        var baseline = Time(disabled.Dispatcher, actions);
        var budget = baseline * 3 + TimeSpan.FromMilliseconds(100);

        var unreachableTime = Time(unreachable.Dispatcher, actions);
        var stalledTime = Time(stalled.Dispatcher, actions);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"disabled {baseline.TotalMilliseconds:F1} ms, unreachable {unreachableTime.TotalMilliseconds:F1} ms, stalled {stalledTime.TotalMilliseconds:F1} ms");

        Assert.InRange(unreachableTime, TimeSpan.Zero, budget);
        Assert.InRange(stalledTime, TimeSpan.Zero, budget);
        Assert.Equal(200 + actions, stalled.Get<IState<CounterState>>().Value.Count);
    }

    private static TimeSpan Time(IDispatcher dispatcher, int count)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
            dispatcher.Dispatch(new IncrementAction());
        return stopwatch.Elapsed;
    }

    private static int Count(Snapshot snapshot) =>
        System.Text.Json.JsonDocument.Parse(snapshot.Features.Single(f => f.Name == "Counter").StateJson)
            .RootElement.GetProperty("Count").GetInt32();
}
