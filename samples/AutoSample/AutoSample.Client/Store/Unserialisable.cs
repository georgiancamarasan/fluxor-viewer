using Fluxor;

namespace AutoSample.Client.Store;

/// <summary>
/// State with a delegate member, which System.Text.Json refuses to serialise.
/// The Viewer should show this Feature as an error while the rest of the Session keeps working.
/// </summary>
[FeatureState]
public sealed record UnserialisableState(int Version, Func<string> Describe)
{
    private UnserialisableState() : this(0, () => "version 0") { }
}

public sealed record BumpUnserialisableAction;

public static class UnserialisableReducers
{
    [ReducerMethod(typeof(BumpUnserialisableAction))]
    public static UnserialisableState Bump(UnserialisableState state)
    {
        var version = state.Version + 1;
        return new(version, () => $"version {version}");
    }
}
