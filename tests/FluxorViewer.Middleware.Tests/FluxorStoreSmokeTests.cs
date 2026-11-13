using Fluxor;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Middleware.Tests;

// PK-1: proves the test harness can host a real Fluxor store; middleware tests build on this.
public class FluxorStoreSmokeTests
{
    [Fact]
    public async Task Store_initializes_and_reduces_a_dispatched_action()
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => o.ScanTypes(typeof(CounterFeature), typeof(CounterReducers)));
        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IStore>();
        await store.InitializeAsync();
        provider.GetRequiredService<IDispatcher>().Dispatch(new IncrementAction());

        var state = provider.GetRequiredService<IState<CounterState>>();
        Assert.Equal(1, state.Value.Count);
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
