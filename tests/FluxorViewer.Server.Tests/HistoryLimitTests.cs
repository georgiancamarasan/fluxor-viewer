using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxorViewer.Server.Tests;

// IN-6: each Session keeps at most its History limit of entries, discarding the oldest first.
public class HistoryLimitTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static Snapshot NewSnapshot(Guid sessionId) => new(
        ProtocolVersion.Current, sessionId, "TestApp", Runtime.Server, DateTimeOffset.UtcNow, LastSequence: 0, DroppedCount: 0,
        [Counter(0), new FeatureState("Weather", "App.WeatherState", """{"Loading":false}""")]);

    private static FeatureState Counter(long count) => new("Counter", "App.CounterState", $$"""{"Count":{{count}}}""");

    private static ActionRecorded NewAction(Guid sessionId, long sequence) => new(
        sessionId, sequence, DateTimeOffset.UtcNow, "App.Increment", "{}", [Counter(sequence)]);

    private static async Task SendActionsAsync(HubConnection connection, Guid sessionId, long from, long to)
    {
        for (var batch = from; batch <= to; batch += 100)
        {
            var actions = Enumerable.Range(0, (int)Math.Min(100, to - batch + 1)).Select(i => NewAction(sessionId, batch + i)).ToArray();
            await connection.InvokeAsync("Actions", actions, Ct);
        }
    }

    [Fact]
    public async Task By_default_a_Session_keeps_its_last_1000_Actions_each_with_its_state_before_and_after()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);

        await SendActionsAsync(connection, sessionId, 1, 1500);

        var actions = viewer.Registry.Find(sessionId)!.Actions.ToList();
        Assert.Equal(Enumerable.Range(501, 1000).Select(i => (long)i), actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":500}""", actions[0].Before["Counter"].StateJson);
        Assert.Equal("""{"Count":501}""", actions[0].After["Counter"].StateJson);
        Assert.Equal("""{"Count":1500}""", actions[^1].After["Counter"].StateJson);
        Assert.Equal("""{"Loading":false}""", actions[0].Before["Weather"].StateJson);
    }

    [Fact]
    public async Task The_history_setting_sets_the_limit()
    {
        await using var limited = new Viewer(new Dictionary<string, string?> { ["history"] = "3" });
        await using var connection = await limited.ConnectAsync();
        var sessionId = Guid.NewGuid();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);

        await SendActionsAsync(connection, sessionId, 1, 5);

        Assert.Equal([3, 4, 5], limited.Registry.Find(sessionId)!.Actions.Select(a => a.Sequence));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    public void A_history_that_is_not_a_whole_number_of_at_least_1_stops_the_Viewer_starting(string value)
    {
        var ex = Assert.ThrowsAny<Exception>(() => new Viewer(new Dictionary<string, string?> { ["history"] = value }));

        Assert.Equal($"--history must be a whole number of at least 1, not \"{value}\".", ex.GetBaseException().Message);
    }
}

// IN-6: what the registry discards and when, with a small History limit.
public class HistoryLimitRegistryTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, TimeProvider.System, new HistoryLimit(3));

    private void Snapshot(long lastSequence, long droppedCount = 0) => registry.SnapshotReceived("c1", new Snapshot(
        ProtocolVersion.Current, SessionId, "MyApp", Runtime.Server, DateTimeOffset.UtcNow, lastSequence, droppedCount,
        [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{lastSequence}}}""")]));

    private void Dispatch(long sequence) => registry.ActionsReceived("c1", [new ActionRecorded(
        SessionId, sequence, DateTimeOffset.UtcNow, "App.Increment", "{}", [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{sequence}}}""")])]);

    private string[] History() =>
        [.. registry.Find(SessionId)!.History.Select(e => e is ResyncEntry ? $"R{e.Sequence}" : $"A{e.Sequence}")];

    [Fact]
    public void Resyncs_count_toward_the_limit_and_are_discarded_in_order_like_Actions()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(5, droppedCount: 4);
        Dispatch(6);
        Dispatch(7);
        Assert.Equal(["R5", "A6", "A7"], History());

        Dispatch(8);
        Assert.Equal(["A6", "A7", "A8"], History());

        Snapshot(10, droppedCount: 2);
        Assert.Equal(["A7", "A8", "R10"], History());
    }

    [Fact]
    public void A_Session_has_discarded_once_its_History_goes_past_the_limit_until_it_is_Cleared()
    {
        Snapshot(0);
        Dispatch(1);
        Dispatch(2);
        Dispatch(3);
        Assert.False(registry.Find(SessionId)!.HasDiscarded);

        Dispatch(4);
        Assert.True(registry.Find(SessionId)!.HasDiscarded);

        registry.Clear(SessionId);
        Assert.False(registry.Find(SessionId)!.HasDiscarded);

        Dispatch(5);
        Dispatch(6);
        Dispatch(7);
        Assert.False(registry.Find(SessionId)!.HasDiscarded);

        Dispatch(8);
        Assert.True(registry.Find(SessionId)!.HasDiscarded);
    }
}
