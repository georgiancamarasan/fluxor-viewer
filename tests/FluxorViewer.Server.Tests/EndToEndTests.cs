using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Server.Tests;

// The real middleware, with a real Fluxor store, reporting to the real Viewer (deferred from MW-2).
public class EndToEndTests(Viewer viewer) : IClassFixture<Viewer>
{
    [Fact]
    public async Task A_store_appears_as_a_Session_with_its_Snapshot_and_Actions()
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), typeof(CounterReducers))
            .UseFluxorViewer(v =>
            {
                v.Enabled = true;
                v.AppName = "EndToEnd";
                v.ServerUrl = viewer.BaseAddress.ToString();
            }));
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStore>().InitializeAsync();

        await Wait.Until(() => viewer.Registry.GetSessions().Any(s => s.AppName == "EndToEnd"));
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        dispatcher.Dispatch(new IncrementAction());
        dispatcher.Dispatch(new IncrementAction());

        var sessionId = viewer.Registry.GetSessions().Single(s => s.AppName == "EndToEnd").SessionId;
        await Wait.Until(() => viewer.Registry.Find(sessionId)!.Actions.Count(a => a.ActionType.EndsWith("IncrementAction")) == 2);
        var session = viewer.Registry.Find(sessionId)!;

        Assert.Equal(Runtime.Server, session.Runtime);
        var counter = Assert.Single(session.Actions.First().Before.Values);
        Assert.Equal("Counter", counter.Name);
        Assert.Equal(typeof(CounterState).FullName, counter.StateType);
        var increments = session.Actions.Where(a => a.ActionType == typeof(IncrementAction).FullName).ToList();
        Assert.Equal([1, 2], increments.Select(a => Count(a.After["Counter"].StateJson)));
        Assert.Equal(increments[0].Sequence + 1, increments[1].Sequence);

        await provider.DisposeAsync();

        await Wait.Until(() => viewer.Registry.Find(sessionId)!.ConnectionId is null);
    }

    [Fact]
    public async Task An_idle_Session_reports_again_from_where_it_was_when_the_Viewer_restarts()
    {
        var first = new Viewer();
        var port = first.BaseAddress.Port;
        var services = new ServiceCollection();
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), typeof(CounterReducers))
            .UseFluxorViewer(v =>
            {
                v.Enabled = true;
                v.AppName = "Restart";
                v.ServerUrl = first.BaseAddress.ToString();
            }));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStore>().InitializeAsync();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        await Wait.Until(() => first.Registry.GetSessions().Any(s => s.AppName == "Restart"));
        var sessionId = first.Registry.GetSessions().Single(s => s.AppName == "Restart").SessionId;
        dispatcher.Dispatch(new IncrementAction());
        await Wait.Until(() => first.Registry.Find(sessionId)!.Actions.Any(a => a.ActionType == typeof(IncrementAction).FullName));

        await first.DisposeAsync();
        await using var second = new Viewer(port);
        // Nothing is dispatched meanwhile: only the closed connection tells the idle middleware to reconnect.
        await Wait.Until(() => second.Registry.Find(sessionId) is { ConnectionId: not null });
        dispatcher.Dispatch(new IncrementAction());
        await Wait.Until(() => second.Registry.Find(sessionId)!.Actions.Any(a => a.ActionType == typeof(IncrementAction).FullName));

        var increment = Assert.Single(second.Registry.Find(sessionId)!.Actions);
        Assert.Equal(1, Count(increment.Before["Counter"].StateJson));
        Assert.Equal(2, Count(increment.After["Counter"].StateJson));
    }

    private static int Count(string stateJson) => JsonDocument.Parse(stateJson).RootElement.GetProperty("Count").GetInt32();
}

public record CounterState(int Count);

public record IncrementAction;

public class CounterFeature : Feature<CounterState>
{
    public override string GetName() => "Counter";
    protected override CounterState GetInitialState() => new(0);
}

public static class CounterReducers
{
    [ReducerMethod]
    public static CounterState Reduce(CounterState state, IncrementAction _) => state with { Count = state.Count + 1 };
}
