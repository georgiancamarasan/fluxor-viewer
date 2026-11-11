using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace FluxorViewer.Server.Tests;

// IN-4: each Action in the History holds the Feature states before and after it.
public class HistoryTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static Snapshot NewSnapshot(Guid sessionId, long lastSequence = 0, string count = "0") => new(
        ProtocolVersion.Current,
        sessionId,
        "TestApp",
        Runtime.Server,
        DateTimeOffset.UtcNow,
        lastSequence,
        DroppedCount: 0,
        [
            new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{count}}}"""),
            new FeatureState("Weather", "App.WeatherState", """{"Loading":false}"""),
        ]);

    private static ActionRecorded NewAction(Guid sessionId, long sequence, params FeatureState[] changed) => new(
        sessionId, sequence, DateTimeOffset.UtcNow, "App.SomeAction", "{}", changed);

    private static FeatureState Counter(int count) => new("Counter", "App.CounterState", $$"""{"Count":{{count}}}""");

    private static FeatureState Weather(bool loading) => new("Weather", "App.WeatherState", $$"""{"Loading":{{(loading ? "true" : "false")}}}""");

    private async Task<(HubConnection Connection, Guid SessionId)> StartAsync(Snapshot? snapshot = null)
    {
        var connection = await viewer.ConnectAsync();
        snapshot ??= NewSnapshot(Guid.NewGuid());
        await connection.InvokeAsync("Snapshot", snapshot, Ct);
        return (connection, snapshot.SessionId);
    }

    private List<ActionEntry> Actions(Guid sessionId) => viewer.Registry.Find(sessionId)!.Actions.ToList();

    [Fact]
    public async Task The_first_Action_starts_from_the_Snapshot_and_replaces_only_its_Changed_Features()
    {
        var (connection, sessionId) = await StartAsync();
        await using var _ = connection;

        await connection.InvokeAsync("Actions", new[] { NewAction(sessionId, 1, Counter(1)) }, Ct);

        var entry = Assert.Single(Actions(sessionId));
        Assert.Equal("""{"Count":0}""", entry.Before["Counter"].StateJson);
        Assert.Equal("""{"Count":1}""", entry.After["Counter"].StateJson);
        Assert.Same(entry.Before["Weather"], entry.After["Weather"]);
    }

    [Fact]
    public async Task Each_Action_starts_from_the_previous_one_and_unchanged_Features_share_one_instance()
    {
        var (connection, sessionId) = await StartAsync();
        await using var _ = connection;

        await connection.InvokeAsync("Actions", new[]
        {
            NewAction(sessionId, 1, Counter(1)),
            NewAction(sessionId, 2, Weather(true)),
            NewAction(sessionId, 3, Counter(2), Weather(false)),
        }, Ct);

        var entries = Actions(sessionId);
        Assert.Same(entries[0].After, entries[1].Before);
        Assert.Same(entries[1].After, entries[2].Before);

        // Weather is untouched by Action 1, Counter by Action 2: each keeps the exact instance it had.
        Assert.Same(entries[0].Before["Weather"].StateJson, entries[0].After["Weather"].StateJson);
        Assert.Same(entries[0].After["Counter"].StateJson, entries[1].After["Counter"].StateJson);
        Assert.Equal(
            [("""{"Count":1}""", """{"Loading":false}"""), ("""{"Count":1}""", """{"Loading":true}"""), ("""{"Count":2}""", """{"Loading":false}""")],
            entries.Select(e => (e.After["Counter"].StateJson, e.After["Weather"].StateJson)));
    }

    [Fact]
    public async Task Each_entry_knows_which_Features_it_changed_even_when_the_state_looks_identical()
    {
        var (connection, sessionId) = await StartAsync();
        await using var _ = connection;

        await connection.InvokeAsync("Actions", new[]
        {
            NewAction(sessionId, 1, Counter(0)),
            NewAction(sessionId, 2),
            NewAction(sessionId, 3, Counter(1), Weather(true)),
        }, Ct);

        var entries = Actions(sessionId);
        Assert.Equal(["Counter"], entries[0].ChangedFeatures);
        Assert.Empty(entries[1].ChangedFeatures);
        Assert.Same(entries[1].Before, entries[1].After);
        Assert.True(entries[2].ChangedFeatures.Contains("Weather"));
        Assert.False(entries[0].ChangedFeatures.Contains("Weather"));
    }

    [Fact]
    public async Task A_Feature_first_seen_in_an_Action_is_added_from_then_on()
    {
        var (connection, sessionId) = await StartAsync();
        await using var _ = connection;
        var cart = new FeatureState("Cart", "App.CartState", """{"Items":[]}""");

        await connection.InvokeAsync("Actions", new[] { NewAction(sessionId, 1, cart), NewAction(sessionId, 2, Counter(1)) }, Ct);

        var entries = Actions(sessionId);
        Assert.False(entries[0].Before.ContainsKey("Cart"));
        Assert.Equal("""{"Items":[]}""", entries[1].After["Cart"].StateJson);
        Assert.Equal(["Cart", "Counter", "Weather"], viewer.Registry.Find(sessionId)!.Current.Keys.Order());
    }

    [Fact]
    public async Task A_later_Snapshot_becomes_the_state_the_next_Action_starts_from()
    {
        var (connection, sessionId) = await StartAsync();
        await using var _ = connection;
        await connection.InvokeAsync("Actions", new[] { NewAction(sessionId, 1, Counter(1)) }, Ct);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 9, count: "9") with { DroppedCount = 8 }, Ct);
        await connection.InvokeAsync("Actions", new[] { NewAction(sessionId, 10, Counter(10)) }, Ct);

        var session = viewer.Registry.Find(sessionId)!;
        Assert.Equal(2, session.History.Count);
        var next = session.Actions.Last();
        Assert.Equal("""{"Count":9}""", next.Before["Counter"].StateJson);
        Assert.Equal("""{"Count":10}""", session.Current["Counter"].StateJson);
        Assert.Equal(10, session.LastSequence);
    }
}
