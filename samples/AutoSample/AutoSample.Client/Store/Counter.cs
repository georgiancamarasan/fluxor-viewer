using Fluxor;

namespace AutoSample.Client.Store;

[FeatureState]
public sealed record CounterState(int Count)
{
    private CounterState() : this(0) { }
}

public sealed record IncrementCounterAction;

public static class CounterReducers
{
    [ReducerMethod(typeof(IncrementCounterAction))]
    public static CounterState Increment(CounterState state) => state with { Count = state.Count + 1 };
}
