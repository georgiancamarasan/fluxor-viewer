using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Middleware.Tests.CounterJson;

namespace FluxorViewer.Middleware.Tests;

// MW-5: sending is batched, bounded, and never makes dispatch wait.
public class SendingTests
{
    [Fact]
    public async Task Queued_Actions_are_sent_in_order_in_batches_of_at_most_100()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection); // Sequence 2 in flight.

        harness.DispatchIncrements(249);
        gate.SetResult();
        var actions = await Harness.ActionsSent(connection, 250);

        Assert.Equal(Enumerable.Range(2, 250).Select(i => (long)i), actions.Select(a => a.Sequence));
        Assert.Equal([1, 100, 100, 49], connection.Batches.Skip(1).Select(b => b.Count)); // After StoreInitializedAction's.
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_Actions_and_resyncs_from_the_state_before_the_rest()
    {
        await using var harness = new Harness(configure: v => v.BufferSize = 10);
        var (connection, first) = await harness.StartConnected();
        var gate = await harness.StallSender(connection); // Sequence 2 in flight.

        harness.DispatchIncrements(49); // 3..51: the buffer keeps the newest 10 (42..51); 3..41 are dropped.
        gate.SetResult();
        await Wait.Until(() => connection.Sent.LastOrDefault() is ActionRecorded { Sequence: 51 });

        // Sent: Snapshot, Actions 1..2, Snapshot (LastSequence 41, 39 dropped), Actions 42..51.
        var sent = connection.Sent.ToList();
        var resync = Assert.IsType<Snapshot>(sent.Single(m => m is Snapshot && !ReferenceEquals(m, first)));
        var before = sent.TakeWhile(m => !ReferenceEquals(m, resync)).OfType<ActionRecorded>().Select(a => a.Sequence).ToList();
        var after = sent.SkipWhile(m => !ReferenceEquals(m, resync)).OfType<ActionRecorded>().Select(a => a.Sequence).ToList();

        Assert.Same(first, sent[0]);
        Assert.Equal([1L, 2L], before);
        Assert.Equal(41, resync.LastSequence);
        Assert.Equal(39, resync.DroppedCount);
        Assert.Equal(40, Count(resync));
        Assert.Equal(Enumerable.Range(42, 10).Select(i => (long)i), after);
        Assert.Equal(first.SessionId, resync.SessionId);
    }

    [Fact]
    public async Task Every_gap_the_Viewer_sees_between_Actions_is_covered_by_a_Snapshot_even_while_dropping_constantly()
    {
        await using var harness = new Harness(configure: v => v.BufferSize = 2);
        var (connection, _) = await harness.StartConnected();

        harness.DispatchIncrements(20_000); // 2..20001, racing the sender with a full buffer.
        await Wait.Until(() => connection.Sent.LastOrDefault() is ActionRecorded { Sequence: 20_001 });

        Assert.Empty(UncoveredGaps(connection.Sent));
    }

    /// <summary>
    /// Each Action whose Sequence doesn't follow on from the Action or Snapshot before it, which the Viewer would build
    /// on the wrong state (ADR 0005).
    /// </summary>
    private static List<long> UncoveredGaps(IEnumerable<object> sent)
    {
        List<long> gaps = [];
        long last = 0;
        foreach (var message in sent)
        {
            if (message is ActionRecorded action && action.Sequence != last + 1)
                gaps.Add(action.Sequence);
            last = message switch
            {
                Snapshot snapshot => snapshot.LastSequence,
                ActionRecorded recorded => recorded.Sequence,
                _ => last,
            };
        }

        return gaps;
    }

    [Fact]
    public async Task The_drop_count_resets_after_each_Snapshot()
    {
        await using var harness = new Harness(configure: v => v.BufferSize = 5);
        var (connection, _) = await harness.StartConnected();

        await Overflow(harness, connection);
        Assert.Equal([0L, 14L], connection.Sent.OfType<Snapshot>().Select(s => s.DroppedCount));
        Assert.Equal([0L, 16L], connection.Sent.OfType<Snapshot>().Select(s => s.LastSequence));

        await Overflow(harness, connection);
        Assert.Equal([0L, 14L, 14L], connection.Sent.OfType<Snapshot>().Select(s => s.DroppedCount));
        Assert.Equal([0L, 16L, 36L], connection.Sent.OfType<Snapshot>().Select(s => s.LastSequence));
        Assert.Equal(13, connection.Actions.Count); // StoreInitializedAction, then 1 + 5 per overflow.
    }

    /// <summary>
    /// With a buffer of 5: puts 1 Action in flight, then 19 more of which the oldest 14 are dropped and the newest 5
    /// kept, and waits for the Snapshot that reports the drop.
    /// </summary>
    private static async Task Overflow(Harness harness, FakeViewer.FakeConnection connection)
    {
        var snapshots = connection.Sent.OfType<Snapshot>().Count();
        var gate = await harness.StallSender(connection);
        harness.DispatchIncrements(19);
        gate.SetResult();
        await Wait.Until(() => connection.Sent.OfType<Snapshot>().Count() > snapshots);
    }

    [Fact]
    public async Task An_Action_that_fails_to_serialise_stays_in_its_batch()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var gate = harness.StallActions();

        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new ExplodingAction());
        harness.Dispatcher.Dispatch(new IncrementAction());
        gate.SetResult();
        var actions = await Harness.ActionsSent(connection, 4);

        Assert.Equal([2L, 3L, 4L, 5L], actions.Select(a => a.Sequence));
        Assert.Contains("$error", actions[2].Payload);
    }

    // Dispatch is synchronous, so an Action that waited on the Viewer would hang the test rather than slow it down.
    [Fact]
    public async Task Dispatching_never_waits_for_a_Viewer_that_is_not_answering()
    {
        await using var connecting = new Harness(configure: v => v.BufferSize = 100); // its connect waits on ConnectGate, never opened
        await connecting.Start();
        await using var stalled = new Harness(configure: v => v.BufferSize = 100);
        await stalled.StartConnected();
        stalled.StallActions();

        connecting.DispatchIncrements(5000);
        stalled.DispatchIncrements(5000);

        Assert.Equal(5000, connecting.Get<IState<CounterState>>().Value.Count);
        Assert.Equal(5000, stalled.Get<IState<CounterState>>().Value.Count);
    }
}
