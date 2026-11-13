using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Middleware.Tests.CounterJson;

namespace FluxorViewer.Middleware.Tests;

// MW-10: an Action that can't be built is a Dropped Action, followed straight away by a Snapshot of the state just after
// it, so the Viewer records a Resync instead of building state across the hole (ADR 0005).
// Each test stalls the sender on Action 2, so the Actions dispatched next are sent as one batch.
public class UnbuildableActionTests
{
    private static readonly Type[] Glitchy = [typeof(GlitchFeature), typeof(GlitchReducers)];

    /// <summary>
    /// What was sent on <paramref name="connection"/> after its first Snapshot and StoreInitializedAction, as Snapshots
    /// and Action Sequences.
    /// </summary>
    private static IEnumerable<string> SentAfterStart(FakeViewer.FakeConnection connection) =>
        connection.Sent.Skip(2).Select(message => message switch
        {
            Snapshot snapshot => $"Snapshot {snapshot.LastSequence}",
            ActionRecorded action => $"{action.Sequence}",
            _ => message.ToString()!,
        });

    /// <summary>The DroppedCount of each Snapshot sent on <paramref name="connection"/> after its first.</summary>
    private static IEnumerable<long> DroppedCounts(FakeViewer.FakeConnection connection) =>
        connection.Sent.OfType<Snapshot>().Skip(1).Select(s => s.DroppedCount);

    /// <summary>The last Snapshot sent on <paramref name="connection"/>.</summary>
    private static Snapshot LastSnapshot(FakeViewer.FakeConnection connection) => connection.Sent.OfType<Snapshot>().Last();

    /// <summary>How many Actions each call on <paramref name="connection"/> carried, after StoreInitializedAction's and Action 2's.</summary>
    private static IEnumerable<int> PartSizes(FakeViewer.FakeConnection connection) => connection.Batches.Skip(2).Select(b => b.Count);

    private static int Glitches(Snapshot snapshot) =>
        JsonDocument.Parse(snapshot.Features.Single(f => f.Name == "Glitch").StateJson).RootElement.GetProperty("Count").GetInt32();

    [Fact]
    public async Task An_Action_that_cant_be_built_is_followed_by_a_Snapshot_just_after_it_and_the_rest_of_its_batch()
    {
        await using var harness = new Harness(types: Glitchy);
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection); // 2: Counter 1.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3: Counter 2.
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 5: Counter 3.
        harness.Get<GlitchFeature>().FailNextNames(1);

        gate.SetResult();
        await Wait.Until(() => connection.Actions.Any(a => a.Sequence == 5));

        Assert.Equal(["2", "3", "Snapshot 4", "5"], SentAfterStart(connection));
        var resync = LastSnapshot(connection);
        Assert.Equal(1, resync.DroppedCount);
        Assert.Equal(2, Count(resync));
        Assert.Equal(1, Glitches(resync));
        Assert.Equal(3, Count(connection.Actions.Last().ChangedFeatures));
        var warning = Assert.Single(harness.Logs.Entries, e => e.Message.Contains("could not build Action 4"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.EndsWith("dropped.", warning.Message);
    }

    [Fact]
    public async Task Actions_in_a_row_that_cant_be_built_share_one_Snapshot_after_the_last()
    {
        await using var harness = new Harness(types: Glitchy);
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection);
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built.
        harness.Dispatcher.Dispatch(new GlitchAction()); // 5: can't be built.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 6
        harness.Get<GlitchFeature>().FailNextNames(2);

        gate.SetResult();
        await Wait.Until(() => connection.Actions.Any(a => a.Sequence == 6));

        Assert.Equal(["2", "3", "Snapshot 5", "6"], SentAfterStart(connection));
        Assert.Equal([2], DroppedCounts(connection));
        Assert.Equal(2, Glitches(LastSnapshot(connection)));
    }

    [Fact]
    public async Task An_Action_that_cant_be_built_at_the_end_of_its_batch_is_followed_by_a_Snapshot()
    {
        await using var harness = new Harness(types: Glitchy);
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection);
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built.
        harness.Get<GlitchFeature>().FailNextNames(1);

        gate.SetResult();
        await Wait.Until(() => DroppedCounts(connection).Any());

        Assert.Equal(["2", "3", "Snapshot 4"], SentAfterStart(connection));
        Assert.Equal([1], DroppedCounts(connection));
    }

    [Fact]
    public async Task The_Snapshot_after_an_Action_that_cant_be_built_goes_between_the_parts_its_batch_is_split_into()
    {
        await using var harness = new Harness(types: Glitchy, configure: v => { v.MaxMessageSize = 1024 * 1024; v.MaxValueSize = 1024 * 1024; });
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection);
        var note = new string('a', 400 * 1024); // Two fit in a 1 MB message, three don't.
        harness.Dispatcher.Dispatch(new NoteAction(note)); // 3
        harness.Dispatcher.Dispatch(new NoteAction(note)); // 4
        harness.Dispatcher.Dispatch(new NoteAction(note)); // 5
        harness.Dispatcher.Dispatch(new GlitchAction()); // 6: can't be built.
        harness.Dispatcher.Dispatch(new NoteAction(note)); // 7
        harness.Get<GlitchFeature>().FailNextNames(1);

        gate.SetResult();
        await Wait.Until(() => connection.Actions.Any(a => a.Sequence == 7));

        Assert.Equal(["2", "3", "4", "5", "Snapshot 6", "7"], SentAfterStart(connection));
        Assert.Equal([2, 1, 1], PartSizes(connection));
    }

    [Fact]
    public async Task Actions_dropped_while_the_batch_is_sent_are_counted_by_the_next_Snapshot_not_the_one_after_the_unbuildable_Action()
    {
        await using var harness = new Harness(types: Glitchy, configure: v => v.BufferSize = 3);
        var (connection, _) = await harness.StartConnected();
        var first = await harness.StallSender(connection);
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 5
        harness.Get<GlitchFeature>().FailNextNames(1);
        var second = harness.StallActions();
        var started = connection.ActionSendsStarted;
        first.SetResult();
        await Wait.Until(() => connection.ActionSendsStarted > started); // Blocked sending 3, before the Snapshot.

        harness.DispatchIncrements(4); // 6 to 9: 6 is dropped from the full buffer.
        second.SetResult();
        await Wait.Until(() => connection.Actions.Any(a => a.Sequence == 9));

        Assert.Equal(["2", "3", "Snapshot 4", "5", "Snapshot 6", "7", "8", "9"], SentAfterStart(connection));
        Assert.Equal([1, 1], DroppedCounts(connection));
    }

    [Fact]
    public async Task When_the_Snapshot_after_an_Action_that_cant_be_built_cant_be_built_either_the_Session_stops()
    {
        await using var harness = new Harness(types: Glitchy);
        var (connection, _) = await harness.StartConnected();
        var gate = await harness.StallSender(connection);
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built, and nor can the Snapshot after it.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 5
        harness.Get<GlitchFeature>().FailAllNames();

        gate.SetResult();
        await connection.Disposed.WaitAsync(Wait.Timeout, Wait.CancellationToken);
        // Past the longest backoff: a retry would have to start within this step.
        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(["2", "3"], SentAfterStart(connection));
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.Logs.Entries, e => e.Message.Contains("could not build the Snapshot")).Level);
        Assert.Single(harness.Viewer.Connections);
    }

    [Fact]
    public async Task When_sending_the_Snapshot_after_an_Action_that_cant_be_built_fails_the_reconnect_resyncs_past_the_whole_batch()
    {
        await using var harness = new Harness(types: Glitchy);
        var (first, _) = await harness.StartConnected();
        var gate = await harness.StallSender(first); // 2: Counter 1.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3: Counter 2.
        harness.Dispatcher.Dispatch(new GlitchAction()); // 4: can't be built.
        harness.Dispatcher.Dispatch(new IncrementAction()); // 5: Counter 3.
        harness.Get<GlitchFeature>().FailNextNames(1);
        harness.Viewer.SnapshotFailure = new HttpRequestException("Connection lost");

        gate.SetResult();
        await first.Disposed.WaitAsync(Wait.Timeout, Wait.CancellationToken);
        harness.Viewer.SnapshotFailure = null;
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);

        Assert.Equal(["2", "3"], SentAfterStart(first));
        var next = Assert.IsType<Snapshot>(await harness.Viewer.Connections.Last().FirstMessage.WaitAsync(Wait.Timeout, Wait.CancellationToken));
        Assert.Equal(5, next.LastSequence);
        Assert.Equal(3, Count(next));
        Assert.Equal(1, Glitches(next));
    }
}

public record GlitchAction;

public record GlitchState(int Count);

/// <summary>A Feature whose name can be made to fail, as a transient fault while building a message.</summary>
public class GlitchFeature : Feature<GlitchState>
{
    private readonly Lock gate = new();
    private int failures;

    /// <summary>The next <paramref name="count"/> reads of the name throw.</summary>
    public void FailNextNames(int count)
    {
        lock (gate)
            failures = count;
    }

    /// <summary>Every read of the name throws from now on.</summary>
    public void FailAllNames() => FailNextNames(int.MaxValue);

    public override string GetName()
    {
        lock (gate)
        {
            if (failures > 0)
            {
                failures--;
                throw new InvalidOperationException("Glitch");
            }
        }

        return "Glitch";
    }

    protected override GlitchState GetInitialState() => new(0);
}

public static class GlitchReducers
{
    [ReducerMethod(typeof(GlitchAction))]
    public static GlitchState Glitch(GlitchState state) => new(state.Count + 1);
}
