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
        var counter = Assert.Single(Assert.Single(session.Snapshots).Features);
        Assert.Equal("Counter", counter.Name);
        Assert.Equal(typeof(CounterState).FullName, counter.StateType);
        var increments = session.Actions.Where(a => a.ActionType == typeof(IncrementAction).FullName).ToList();
        Assert.Equal([1, 2], increments.Select(a => JsonDocument.Parse(a.ChangedFeatures.Single().StateJson).RootElement.GetProperty("Count").GetInt32()));
        Assert.Equal(increments[0].Sequence + 1, increments[1].Sequence);

        await provider.DisposeAsync();

        await Wait.Until(() => viewer.Registry.Find(sessionId)!.ConnectionId is null);
    }
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
