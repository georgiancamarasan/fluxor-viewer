using Fluxor;
using FluxorViewer.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Server.Tests;

// MW-9: what the middleware sends to stay under the Viewer's 32 MB message limit really is under it, on the real hub.
// These hold hundreds of MB, so they run alone rather than alongside other test classes.
[Collection(typeof(LargeMessages))]
public class MessageSizeEndToEndTests(Viewer viewer) : IClassFixture<Viewer>
{
    /// <summary>
    /// A store reporting as <paramref name="appName"/>, with MaxValueSize at the 32 MB limit, to the Viewer, or to whatever
    /// listens at <paramref name="serverUrl"/>.
    /// </summary>
    private ServiceProvider Store(string appName, Uri? serverUrl = null)
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => o
            .ScanTypes(typeof(ListFeature), typeof(ListReducers))
            .UseFluxorViewer(v =>
            {
                v.Enabled = true;
                v.AppName = appName;
                v.ServerUrl = (serverUrl ?? viewer.BaseAddress).ToString();
                v.MaxValueSize = 32 * 1024 * 1024;
            }));
        return services.BuildServiceProvider();
    }

    /// <summary>Starts the store, waits for its Session to reach the Viewer, and returns its SessionId.</summary>
    private async Task<Guid> StartSession(ServiceProvider provider, string appName)
    {
        await provider.GetRequiredService<IStore>().InitializeAsync();
        await Wait.Until(() => viewer.Registry.GetSessions().Any(s => s.AppName == appName));
        return viewer.Registry.GetSessions().Single(s => s.AppName == appName).SessionId;
    }

    /// <summary>Dispatches <paramref name="count"/> Notes of <paramref name="length"/> characters each.</summary>
    private static void DispatchNotes(ServiceProvider provider, int count, int length)
    {
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        for (var i = 0; i < count; i++)
            dispatcher.Dispatch(new NoteAction(new string('a', length)));
    }

    [Fact]
    public async Task A_16_MB_Feature_state_made_of_quotes_reaches_the_Viewer_whole()
    {
        await using var provider = Store("QuoteHeavy");
        var sessionId = await StartSession(provider, "QuoteHeavy");

        provider.GetRequiredService<IDispatcher>().Dispatch(new FillAction(4_000_000)); // ["a","a",…]: half of it quotes

        await Wait.Until(() => viewer.Registry.Find(sessionId)!.Actions.Any(a => a.ActionType == typeof(FillAction).FullName));
        var fill = viewer.Registry.Find(sessionId)!.Actions.Single(a => a.ActionType == typeof(FillAction).FullName);
        Assert.Equal(16_000_011, fill.After["List"].StateJson.Length); // {"Items":[ + 4,000,000 × "a" and commas + ]}
    }

    [Fact]
    public async Task Forty_MB_of_Actions_waiting_for_the_Viewer_all_reach_it()
    {
        var down = new Viewer();
        await down.DisposeAsync();
        await using var provider = Store("Backlog", down.BaseAddress);
        await provider.GetRequiredService<IStore>().InitializeAsync();
        DispatchNotes(provider, 8, 5 * 1024 * 1024);

        await using var up = new Viewer(down.BaseAddress.Port);

        await Wait.Until(() => up.Registry.GetSessions().Any(s => s.AppName == "Backlog" &&
            s.Actions.Count(a => a.ActionType == typeof(NoteAction).FullName) == 8));
    }
}

/// <summary>Tests that need tens of MB at a time: they run on their own, not in parallel with other test classes.</summary>
[CollectionDefinition(DisableParallelization = true)]
public class LargeMessages;

public record NoteAction(string Text);

public record ListState(IReadOnlyList<string> Items);

/// <summary>Fills the List Feature with <paramref name="Count"/> items of <c>"a"</c>.</summary>
public record FillAction(int Count);

public class ListFeature : Feature<ListState>
{
    public override string GetName() => "List";
    protected override ListState GetInitialState() => new([]);
}

public static class ListReducers
{
    [ReducerMethod]
    public static ListState Fill(ListState _, FillAction action) => new(Enumerable.Repeat("a", action.Count).ToList());
}
