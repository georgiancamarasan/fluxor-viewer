using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-3: every dispatched Action is reported with only its Changed Features.
public class ActionRecordingTests
{
    private static CancellationToken Ct => Wait.Ct;

    private static int Count(FeatureState feature) => JsonDocument.Parse(feature.StateJson).RootElement.GetProperty("Count").GetInt32();

    [Fact]
    public async Task Reports_only_the_Features_whose_state_changed()
    {
        await using var harness = new Harness();
        var (connection, snapshot) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(snapshot.SessionId, action.SessionId);
        Assert.Equal(typeof(IncrementAction).FullName, action.ActionType);
        Assert.Equal("{}", action.ActionJson);
        var counter = Assert.Single(action.ChangedFeatures);
        Assert.Equal("Counter", counter.Name);
        Assert.Equal(typeof(CounterState).FullName, counter.StateType);
        Assert.Equal(1, Count(counter));
    }

    [Fact]
    public async Task An_Action_that_changes_nothing_is_still_reported()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new NothingAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(typeof(NothingAction).FullName, action.ActionType);
        Assert.Empty(action.ChangedFeatures);
    }

    [Fact]
    public async Task A_reducer_returning_the_same_instance_is_not_a_change_but_an_equal_new_one_is()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new KeepNameAction());
        harness.Dispatcher.Dispatch(new RenameAction("Ada")); // Same value, new instance.

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Empty(actions[0].ChangedFeatures);
        Assert.Equal("Profile", Assert.Single(actions[1].ChangedFeatures).Name);
    }

    [Fact]
    public async Task Actions_dispatched_by_Effects_are_reported_in_order()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new GoAction());

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Equal([typeof(GoAction).FullName, typeof(IncrementAction).FullName], actions.Select(a => a.ActionType));
    }

    [Fact]
    public async Task Fluxor_internal_Actions_take_a_Sequence_like_any_other()
    {
        await using var harness = new Harness();

        var (_, snapshot) = await harness.StartConnected();

        Assert.Equal(1, snapshot.LastSequence); // StoreInitializedAction.
    }

    [Fact]
    public async Task Fluxor_internal_Actions_are_reported_once_connected()
    {
        // A connection that opens synchronously is connected before Fluxor dispatches StoreInitializedAction.
        await using var harness = new Harness(connectImmediately: true);
        var connection = await harness.Start();

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));

        Assert.Equal(0, Assert.IsType<Snapshot>(connection.Sent.First()).LastSequence);
        Assert.Equal("Fluxor.StoreInitializedAction", action.ActionType);
        Assert.Equal(1, action.Sequence);
    }

    [Fact]
    public async Task Sequences_follow_the_Snapshot_without_gaps_or_repeats()
    {
        await using var harness = new Harness();
        var connection = await harness.Start();
        harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Dispatcher.Dispatch(new IncrementAction());

        harness.Viewer.ConnectGate.SetResult();
        var snapshot = Assert.IsType<Snapshot>(await connection.FirstMessage.WaitAsync(Wait.Timeout, Ct));
        for (var i = 0; i < 3; i++)
            harness.Dispatcher.Dispatch(new IncrementAction());
        var actions = await Harness.ActionsSent(connection, 3);

        Assert.Equal(3, snapshot.LastSequence); // StoreInitializedAction + 2 increments, all covered by the Snapshot.
        Assert.Equal(2, Count(Assert.Single(snapshot.Features, f => f.Name == "Counter")));
        Assert.IsType<Snapshot>(connection.Sent.First());
        Assert.Equal([4L, 5L, 6L], actions.Select(a => a.Sequence));
        Assert.Equal([3, 4, 5], actions.Select(a => Count(Assert.Single(a.ChangedFeatures))));
    }

    [Fact]
    public async Task Timestamps_are_UTC_at_dispatch()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var before = DateTimeOffset.UtcNow;

        harness.Dispatcher.Dispatch(new IncrementAction());
        var after = DateTimeOffset.UtcNow;

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.InRange(action.Timestamp, before, after);
        Assert.Equal(TimeSpan.Zero, action.Timestamp.Offset);
    }

    [Fact]
    public async Task ActionType_is_a_readable_name_for_nested_and_generic_Actions()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new Items.LoadedAction<Item>([new Item("a")]));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("FluxorViewer.Middleware.Tests.Items.LoadedAction<FluxorViewer.Middleware.Tests.Item>", action.ActionType);
    }

    [Fact]
    public async Task After_a_failed_send_the_Session_stops_reporting()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        harness.Viewer.ActionsFailure = new HttpRequestException("Gone");

        harness.Dispatcher.Dispatch(new IncrementAction());
        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Exception is HttpRequestException));
        harness.Viewer.ActionsFailure = null;
        harness.Dispatcher.Dispatch(new IncrementAction());
        await Task.Delay(100, Ct);

        Assert.Empty(connection.Actions);
        Assert.Equal(LogLevel.Debug, harness.Logs.Entries.Single(e => e.Exception is HttpRequestException).Level);
        Assert.Equal(2, harness.Get<IState<CounterState>>().Value.Count);
    }

    [Fact]
    public async Task An_exception_while_recording_never_reaches_dispatch()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var flaky = harness.Get<FlakyFeature>();

        flaky.Throw = true;
        harness.Dispatcher.Dispatch(new IncrementAction());
        flaky.Throw = false;
        harness.Dispatcher.Dispatch(new IncrementAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(3, action.Sequence); // 2 was lost while recording.
        Assert.Equal(2, Count(Assert.Single(action.ChangedFeatures, f => f.Name == "Counter")));
        var debug = Assert.Single(harness.Logs.Entries, e => e.Exception is FlakyException);
        Assert.Equal(LogLevel.Debug, debug.Level);
    }

    [Theory]
    [InlineData(typeof(IncrementAction), "FluxorViewer.Middleware.Tests.IncrementAction")]
    [InlineData(typeof(Items.LoadedAction<Item>), "FluxorViewer.Middleware.Tests.Items.LoadedAction<FluxorViewer.Middleware.Tests.Item>")]
    [InlineData(typeof(Outer<int>.Inner<string>), "FluxorViewer.Middleware.Tests.Outer<System.Int32>.Inner<System.String>")]
    [InlineData(typeof(Dictionary<string, List<int>>), "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>")]
    [InlineData(typeof(int?), "System.Nullable<System.Int32>")]
    [InlineData(typeof(Item[]), "FluxorViewer.Middleware.Tests.Item[]")]
    [InlineData(typeof(int[,]), "System.Int32[,]")]
    public void Readable_type_names(Type type, string expected) => Assert.Equal(expected, TypeNames.Readable(type));
}

public record NothingAction;

public record GoAction;

public record KeepNameAction;

public record RenameAction(string Name);

public record Item(string Id);

public static class Items
{
    public record LoadedAction<T>(IReadOnlyList<T> Items);
}

public static class Outer<T>
{
    public record Inner<TInner>;
}

public class ExplodingAction
{
    public int Value => throw new InvalidOperationException("Boom");
}

public static class ProfileReducers
{
    [ReducerMethod]
    public static ProfileState Keep(ProfileState state, KeepNameAction _) => state;

    [ReducerMethod]
    public static ProfileState Rename(ProfileState state, RenameAction action) => new(action.Name);
}

public class GoEffects
{
    [EffectMethod]
    public Task Handle(GoAction _, IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new IncrementAction());
        return Task.CompletedTask;
    }
}

public record FlakyState;

public class FlakyException() : Exception("Flaky");

/// <summary>A Feature whose GetState can be made to throw, to simulate a misbehaving custom Feature.</summary>
public class FlakyFeature : Feature<FlakyState>
{
    public bool Throw { get; set; }

    public override string GetName() => "Flaky";

    protected override FlakyState GetInitialState() => new();

    public override object GetState() => Throw ? throw new FlakyException() : base.GetState();
}
