using Fluxor;

namespace AutoSample.Client.Store;

/// <summary>A list loaded through an Effect, so the Viewer sees Effect-dispatched Actions.</summary>
[FeatureState]
public sealed record ItemsState(bool IsLoading, IReadOnlyList<Item> Items)
{
    private ItemsState() : this(false, []) { }
}

public sealed record Item(int Id, string Name);

public sealed record FetchItemsAction;

public sealed record ItemsLoadedAction(IReadOnlyList<Item> Items);

public static class ItemsReducers
{
    [ReducerMethod(typeof(FetchItemsAction))]
    public static ItemsState Fetch(ItemsState state) => state with { IsLoading = true };

    [ReducerMethod]
    public static ItemsState Loaded(ItemsState state, ItemsLoadedAction action) =>
        new(IsLoading: false, action.Items);
}

public static class ItemsEffects
{
    private static readonly string[] Names = ["Apple", "Banana", "Cherry", "Damson", "Elderberry", "Fig", "Grape"];

    [EffectMethod(typeof(FetchItemsAction))]
    public static async Task Fetch(IDispatcher dispatcher)
    {
        await Task.Delay(500);
        var items = Enumerable.Range(1, Random.Shared.Next(3, 8))
            .Select(i => new Item(i, Names[Random.Shared.Next(Names.Length)]))
            .ToArray();
        dispatcher.Dispatch(new ItemsLoadedAction(items));
    }
}
