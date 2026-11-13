using System.Text.Json;
using System.Text.Json.Serialization;
using Fluxor;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-4: a value that won't serialise becomes a Serialisation error; the rest of the message, and the app, carry on.
public class SerialisationTests
{
    private static readonly Type[] Broken = [typeof(BrokenFeature), typeof(BrokenReducers)];

    /// <summary>The message when <paramref name="json"/> is exactly <c>{"$error": "message"}</c>.</summary>
    private static string Error(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("$error", property.Name);
        return property.Value.GetString()!;
    }

    [Fact]
    public async Task A_Payload_that_fails_to_serialise_is_sent_as_a_Serialisation_error()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new ExplodingAction());
        harness.Dispatcher.Dispatch(new IncrementAction());

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Equal([2L, 3L], actions.Select(a => a.Sequence));
        Assert.Equal("FluxorViewer.Middleware.Tests.ExplodingAction", actions[0].ActionType);
        Assert.Equal("Boom", Error(actions[0].Payload));
    }

    [Fact]
    public async Task A_Changed_Feature_that_fails_to_serialise_is_sent_as_a_Serialisation_error_beside_the_others()
    {
        await using var harness = new Harness(types: Broken);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("{}", action.Payload);
        var broken = Assert.Single(action.ChangedFeatures, f => f.Name == "Broken");
        Assert.Equal("FluxorViewer.Middleware.Tests.BrokenState", broken.StateType);
        Assert.Equal("Converter says no", Error(broken.StateJson));
        var counter = Assert.Single(action.ChangedFeatures, f => f.Name == "Counter");
        Assert.Equal("""{"Count":1}""", counter.StateJson);
    }

    [Fact]
    public async Task A_Snapshot_with_a_Feature_that_fails_to_serialise_is_still_sent_and_the_Session_goes_on()
    {
        await using var harness = new Harness(types: Broken);

        var (connection, snapshot) = await harness.StartConnected();
        Assert.Equal("Converter says no", Error(Assert.Single(snapshot.Features, f => f.Name == "Broken").StateJson));
        Assert.Equal("""{"Count":0}""", Assert.Single(snapshot.Features, f => f.Name == "Counter").StateJson);

        harness.Dispatcher.Dispatch(new NothingAction());
        Assert.Single(await Harness.ActionsSent(connection, 1));
    }

    /// <summary>A chain of <paramref name="length"/> Nodes, nested that deep when serialised.</summary>
    private static Node Chain(int length) => Enumerable.Range(1, length).Aggregate((Node?)null, (next, _) => new Node(next))!;

    [Fact]
    public async Task A_Payload_deeper_than_MaxDepth_says_so_instead_of_suggesting_a_cycle()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new DeepAction(Chain(70)));

        var message = Error(Assert.Single(await Harness.ActionsSent(connection, 1)).Payload);
        Assert.StartsWith("Nested deeper than MaxDepth (64) at $.Root.Next.Next.", message);
        Assert.DoesNotContain("cycle", message);
    }

    [Fact]
    public async Task MaxDepth_is_the_configured_limit()
    {
        await using var harness = new Harness(configure: v => v.MaxDepth = 100);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new DeepAction(Chain(70)));
        harness.Dispatcher.Dispatch(new DeepAction(Chain(120)));

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Equal(70, JsonDocument.Parse(actions[0].Payload, new JsonDocumentOptions { MaxDepth = 100 }).RootElement.ToString().Split("Next").Length - 1);
        Assert.StartsWith("Nested deeper than MaxDepth (100) at $.Root.Next.", Error(actions[1].Payload));
    }

    [Fact]
    public async Task A_cycle_is_sent_as_null()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var loop = new Loop();
        loop.Self = loop;

        harness.Dispatcher.Dispatch(new LoopAction(loop));

        Assert.Equal("""{"Loop":{"Self":null}}""", Assert.Single(await Harness.ActionsSent(connection, 1)).Payload);
    }

    [Fact]
    public async Task Dispatch_never_sees_a_throwing_converter()
    {
        await using var harness = new Harness(types: Broken);
        var (connection, _) = await harness.StartConnected();

        harness.DispatchIncrements(3);

        await Harness.ActionsSent(connection, 3);
        Assert.Equal(3, harness.Get<IState<BrokenState>>().Value.Count);
        Assert.Equal(3, harness.Get<IState<CounterState>>().Value.Count);
    }

    [Fact]
    public async Task A_long_message_is_cut_to_500_characters()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new LongWindedAction());

        var message = Error(Assert.Single(await Harness.ActionsSent(connection, 1)).Payload);
        Assert.Equal(new string('x', 499) + "…", message);
    }

    // "Once" is per process, so these use types no other test fails on.
    [Fact]
    public async Task A_failing_Action_type_is_warned_about_once_then_logged_at_Debug()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new GrumpyAction());
        harness.Dispatcher.Dispatch(new GrumpyAction());
        await Harness.ActionsSent(connection, 2);

        var entries = harness.Logs.Entries.Where(e => e.Exception is GrumpyException).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], entries.Select(e => e.Level));
        Assert.Contains("FluxorViewer.Middleware.Tests.GrumpyAction", entries[0].Message);
    }

    [Fact]
    public async Task A_failing_Feature_is_warned_about_once_then_logged_at_Debug()
    {
        await using var harness = new Harness(types: [typeof(SulkyFeature), typeof(SulkyReducers)]);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new IncrementAction());
        await Harness.ActionsSent(connection, 1);

        // The Snapshot, then the Action.
        var entries = harness.Logs.Entries.Where(e => e.Exception is GrumpyException).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], entries.Select(e => e.Level));
        Assert.Contains("Sulky", entries[0].Message);
    }
}

public class GrumpyException() : Exception("Grumpy");

public class GrumpyAction
{
    public int Value => throw new GrumpyException();
}

public class SulkyState(int count)
{
    public int Count { get; } = count;

    public int Mood => throw new GrumpyException();
}

public class SulkyFeature : Feature<SulkyState>
{
    public override string GetName() => "Sulky";

    protected override SulkyState GetInitialState() => new(0);
}

public static class SulkyReducers
{
    [ReducerMethod]
    public static SulkyState Increment(SulkyState state, IncrementAction _) => new(state.Count + 1);
}

public class Loop
{
    public Loop? Self { get; set; }
}

public record LoopAction(Loop Loop);

public class LongWindedAction
{
    public int Value => throw new InvalidOperationException(new string('x', 2000));
}

public class Node(Node? next)
{
    public Node? Next { get; } = next;
}

public record DeepAction(Node Root);

[JsonConverter(typeof(ThrowingConverter))]
public record BrokenState(int Count);

public class ThrowingConverter : JsonConverter<BrokenState>
{
    public override BrokenState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, BrokenState value, JsonSerializerOptions options) =>
        throw new InvalidOperationException("Converter says no");
}

public class BrokenFeature : Feature<BrokenState>
{
    public override string GetName() => "Broken";

    protected override BrokenState GetInitialState() => new(0);
}

public static class BrokenReducers
{
    [ReducerMethod]
    public static BrokenState Increment(BrokenState state, IncrementAction _) => state with { Count = state.Count + 1 };
}
