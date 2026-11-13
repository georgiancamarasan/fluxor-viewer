using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-3: Sessions are created, reconnect, disconnect and are cleared through the real ingest hub.
public class SessionLifecycleTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static readonly DateTimeOffset StartedAt = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Opens a connection that reports <paramref name="sessionId"/>'s first Snapshot.</summary>
    private async Task<HubConnection> ReportAsync(Guid sessionId)
    {
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        return connection;
    }

    private ReceivedSession Session(Guid sessionId) => viewer.Registry.Find(sessionId) ?? throw new Xunit.Sdk.XunitException("Session not found.");

    [Fact]
    public async Task The_first_Snapshot_creates_a_connected_Session()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.Parse("3f2a0000-0000-0000-0000-000000000001");

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { AppName = "MyApp", Runtime = Runtime.WebAssembly, StartedAt = StartedAt }, Ct);

        var session = Session(sessionId);
        Assert.Equal("MyApp", session.AppName);
        Assert.Equal(Runtime.WebAssembly, session.Runtime);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero), session.StartedAt);
        Assert.Equal("3f2a", session.ShortId);
        Assert.Equal(SessionStatus.Connected, session.Status);
    }

    [Fact]
    public async Task A_closed_connection_leaves_the_Session_disconnected_but_kept()
    {
        var sessionId = Guid.NewGuid();
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1) }, Ct);

        await connection.DisposeAsync();

        await Wait.Until(() => Session(sessionId).Status == SessionStatus.Disconnected);
        Assert.Single(Session(sessionId).Actions);
    }

    [Fact]
    public async Task A_reconnect_reuses_the_Session_keeps_its_first_info_and_marks_it_connected()
    {
        var sessionId = Guid.NewGuid();
        var first = await viewer.ConnectAsync();
        await first.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { AppName = "MyApp" }, Ct);
        await first.DisposeAsync();
        await Wait.Until(() => Session(sessionId).Status == SessionStatus.Disconnected);

        await using var second = await viewer.ConnectAsync();
        await second.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { AppName = "Renamed", Runtime = Runtime.WebAssembly }, Ct);

        var session = Session(sessionId);
        Assert.Equal(SessionStatus.Connected, session.Status);
        Assert.Equal("MyApp", session.AppName);
        Assert.Equal(Runtime.Server, session.Runtime);
        Assert.Single(viewer.Registry.GetSessions(), s => s.SessionId == sessionId);
    }

    [Fact]
    public async Task A_replaced_connection_neither_disconnects_the_Session_nor_adds_Actions()
    {
        var sessionId = Guid.NewGuid();
        var old = await viewer.ConnectAsync();
        await old.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await using var current = await viewer.ConnectAsync();
        await current.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { LastSequence = 5 }, Ct);

        await old.InvokeAsync("Actions", new[] { Increment(sessionId, 4) }, Ct);
        Assert.Empty(Session(sessionId).Actions);

        // An ignored close has nothing to wait on, so the newer connection's next batch is the point to look from.
        await old.DisposeAsync();
        await current.InvokeAsync("Actions", new[] { Increment(sessionId, 6) }, Ct);
        var session = Session(sessionId);
        Assert.Equal(SessionStatus.Connected, session.Status);
        Assert.Equal(current.ConnectionId, session.ConnectionId);
        Assert.Equal([6L], session.Actions.Select(a => a.Sequence));
    }

    [Fact]
    public async Task Clearing_a_disconnected_Session_removes_it_until_it_reconnects()
    {
        var sessionId = Guid.NewGuid();
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1) }, Ct);
        await connection.DisposeAsync();
        await Wait.Until(() => Session(sessionId).Status == SessionStatus.Disconnected);

        viewer.Registry.Clear(sessionId);

        Assert.Null(viewer.Registry.Find(sessionId));
        Assert.DoesNotContain(viewer.Registry.GetSessions(), s => s.SessionId == sessionId);

        await using var again = await viewer.ConnectAsync();
        await again.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { LastSequence = 1 }, Ct);

        var session = Session(sessionId);
        Assert.Empty(session.History);
        Assert.Equal(1, session.LastSequence);
    }

    [Fact]
    public async Task Clearing_a_connected_Session_keeps_its_current_state_and_goes_on_recording()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await viewer.ConnectAsync();
        var weather = new FeatureState("Weather", "App.WeatherState", """{"Loading":false}""");
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { Features = [Counter(0), weather] }, Ct);
        await connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1), Increment(sessionId, 2) }, Ct);

        viewer.Registry.Clear(sessionId);

        var cleared = Session(sessionId);
        Assert.Equal(SessionStatus.Connected, cleared.Status);
        Assert.Empty(cleared.History);
        Assert.Equal(2, cleared.LastSequence);
        Assert.Equal("""{"Count":2}""", cleared.Current["Counter"].StateJson);
        Assert.Equal("""{"Loading":false}""", cleared.Current["Weather"].StateJson);

        await connection.InvokeAsync("Actions", new[] { Increment(sessionId, 3) }, Ct);

        var next = Assert.Single(Session(sessionId).Actions);
        Assert.Equal(3, next.Sequence);
        Assert.Equal("""{"Count":2}""", next.Before["Counter"].StateJson);
    }

    [Fact]
    public async Task Clearing_an_unknown_Session_leaves_the_others_alone()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1) }, Ct);

        viewer.Registry.Clear(Guid.NewGuid());

        Assert.Single(Session(sessionId).Actions);
    }

    [Fact]
    public async Task Sessions_are_listed_in_the_order_they_were_first_seen()
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using var first = await ReportAsync(ids[0]);
        await using var second = await ReportAsync(ids[1]);
        await using var third = await ReportAsync(ids[2]);

        await using var reconnect = await ReportAsync(ids[0]);

        Assert.Equal(ids, viewer.Registry.GetSessions().Select(s => s.SessionId).Where(ids.Contains)); // the first didn't move
    }

    [Fact]
    public async Task Many_Sessions_reporting_reconnecting_and_clearing_at_once_stay_consistent()
    {
        Guid[] reconnecting = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];
        Guid[] clearing = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];

        await Task.WhenAll([.. reconnecting.Select(ReportThenReconnect), .. clearing.Select(ReportClearingAfterEachBatchThenClose)]);

        await Wait.Until(() => clearing.All(id => Session(id).Status == SessionStatus.Disconnected));
        Assert.All(reconnecting.Select(Session), session =>
        {
            Assert.Equal(SessionStatus.Connected, session.Status);
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], session.Actions.Select(a => a.Sequence));
            Assert.Equal(20, session.LastSequence);
        });
        Assert.All(clearing.Select(Session), session =>
        {
            // Cleared after its last batch, while still connected.
            Assert.Empty(session.History);
            Assert.Equal(20, session.LastSequence);
            Assert.Equal("""{"Count":20}""", session.Current["Counter"].StateJson);
        });
    }

    /// <summary>Reports Actions 1–20, closes, and reconnects from where it was.</summary>
    private async Task ReportThenReconnect(Guid sessionId)
    {
        await using (var connection = await viewer.ConnectAsync())
        {
            await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
            await SendTenBatches(connection, sessionId, afterEach: () => { });
        }

        var again = await viewer.ConnectAsync(); // left open: the Session stays connected for the assertions
        await again.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { LastSequence = 20 }, Ct);
    }

    /// <summary>Reports Actions 1–20, Clearing the Session after each batch, then closes.</summary>
    private async Task ReportClearingAfterEachBatchThenClose(Guid sessionId)
    {
        await using var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await SendTenBatches(connection, sessionId, afterEach: () => viewer.Registry.Clear(sessionId));
    }

    /// <summary>
    /// Sends Actions 1–20 in batches of two, running <paramref name="afterEach"/> and reading the Session list between
    /// batches, so registry reads and Clears race the other Sessions' writes.
    /// </summary>
    private async Task SendTenBatches(HubConnection connection, Guid sessionId, Action afterEach)
    {
        for (var sequence = 1; sequence <= 20; sequence += 2)
        {
            await connection.InvokeAsync("Actions", new[] { Increment(sessionId, sequence), Increment(sessionId, sequence + 1) }, Ct);
            afterEach();
            _ = viewer.Registry.GetSessions();
        }
    }
}
