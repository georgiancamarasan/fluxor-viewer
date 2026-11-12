using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Middleware.Tests.CounterJson;

namespace FluxorViewer.Middleware.Tests;

// MW-3: every dispatched Action is reported with only its Changed Features.
public class ActionRecordingTests
{
    private static CancellationToken Ct => Wait.Ct;

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

        var (connection, snapshot) = await harness.StartConnected();

        Assert.Equal(0, snapshot.LastSequence);
        var action = Assert.Single(connection.Actions);
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
        await Harness.ActionsSent(connection, 5);

        // Buffered before the connect: StoreInitializedAction and 2 increments, sent after the Snapshot.
        Assert.Equal(0, snapshot.LastSequence);
        Assert.Equal(0, Count(Assert.Single(snapshot.Features, f => f.Name == "Counter")));
        Assert.IsType<Snapshot>(connection.Sent.First());
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], connection.Actions.Select(a => a.Sequence));
        Assert.Equal([1, 2, 3, 4, 5], connection.Actions.Skip(1).Select(a => Count(Assert.Single(a.ChangedFeatures))));
    }

    [Fact]
    public async Task Timestamps_are_the_UTC_time_of_dispatch()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        harness.Time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 10, 2, 3, 45, TimeSpan.Zero));

        harness.Dispatcher.Dispatch(new IncrementAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 10, 2, 3, 45, TimeSpan.Zero), action.Timestamp);
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

    [Fact]
    public async Task A_state_change_in_an_Action_that_failed_to_record_goes_with_the_next_Action()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var flaky = harness.Get<FlakyFeature>();
        flaky.Throw = true;
        harness.Dispatcher.Dispatch(new IncrementAction()); // Counter 0 → 1, but recording fails.
        flaky.Throw = false;

        harness.Dispatcher.Dispatch(new RenameAction("Grace"));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(["Counter", "Profile"], action.ChangedFeatures.Select(f => f.Name).Order());
        Assert.Equal(1, Count(action.ChangedFeatures.Single(f => f.Name == "Counter")));
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
