using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace FluxorViewer.Server.Tests;

// IN-3: Sessions are created, reconnect, disconnect and are cleared through the real ingest hub.
public class SessionLifecycleTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static Snapshot NewSnapshot(Guid sessionId, string appName = "TestApp", Runtime runtime = Runtime.Server, long lastSequence = 0) => new(
        ProtocolVersion.Current,
        sessionId,
        appName,
        runtime,
        new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero),
        lastSequence,
        DroppedCount: 0,
        [
            new FeatureState("Counter", "App.CounterState", """{"Count":0}"""),
            new FeatureState("Weather", "App.WeatherState", """{"Loading":false}"""),
        ]);

    private static ActionRecorded Increment(Guid sessionId, long sequence) => new(
        sessionId, sequence, DateTimeOffset.UtcNow, "App.IncrementAction", "{}",
        [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{sequence}}}""")]);

    private ReceivedSession Session(Guid sessionId) => viewer.Registry.Find(sessionId) ?? throw new Xunit.Sdk.XunitException("Session not found.");

    [Fact]
    public async Task The_first_Snapshot_creates_a_connected_Session()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.Parse("3f2a0000-0000-0000-0000-000000000001");

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, "MyApp", Runtime.WebAssembly), Ct);

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
        await first.InvokeAsync("Snapshot", NewSnapshot(sessionId, "MyApp"), Ct);
        await first.DisposeAsync();
        await Wait.Until(() => Session(sessionId).Status == SessionStatus.Disconnected);

        await using var second = await viewer.ConnectAsync();
        await second.InvokeAsync("Snapshot", NewSnapshot(sessionId, "Renamed", Runtime.WebAssembly), Ct);

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
        await current.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 5), Ct);

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
        await again.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 1), Ct);

        var session = Session(sessionId);
        Assert.Empty(session.History);
        Assert.Equal(1, session.LastSequence);
    }

    [Fact]
    public async Task Clearing_a_connected_Session_keeps_its_current_state_and_goes_on_recording()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
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
    public void Clearing_an_unknown_Session_does_nothing()
    {
        var before = viewer.Registry.GetSessions().Count;

        viewer.Registry.Clear(Guid.NewGuid());

        Assert.Equal(before, viewer.Registry.GetSessions().Count);
    }

    [Fact]
    public async Task Sessions_are_listed_in_the_order_they_were_first_seen()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var connections = new List<HubConnection>();
        foreach (var id in ids)
        {
            var connection = await viewer.ConnectAsync();
            connections.Add(connection);
            await connection.InvokeAsync("Snapshot", NewSnapshot(id), Ct);
        }

        // A reconnect of the first doesn't move it.
        await using var reconnect = await viewer.ConnectAsync();
        await reconnect.InvokeAsync("Snapshot", NewSnapshot(ids[0]), Ct);

        Assert.Equal(ids, viewer.Registry.GetSessions().Select(s => s.SessionId).Where(ids.Contains));
        foreach (var connection in connections)
            await connection.DisposeAsync();
    }

    [Fact]
    public async Task Many_Sessions_reporting_reconnecting_and_clearing_at_once_stay_consistent()
    {
        const int sessionCount = 20;
        const int batches = 10;
        var ids = Enumerable.Range(0, sessionCount).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(ids.Select(async (id, i) =>
        {
            var connection = await viewer.ConnectAsync();
            await connection.InvokeAsync("Snapshot", NewSnapshot(id), Ct);
            for (var b = 0; b < batches; b++)
            {
                await connection.InvokeAsync("Actions", new[] { Increment(id, 2 * b + 1), Increment(id, 2 * b + 2) }, Ct);
                if (i % 2 == 0)
                    viewer.Registry.Clear(id);
                _ = viewer.Registry.GetSessions();
            }

            await connection.DisposeAsync();

            // Odd Sessions reconnect after closing; even ones stay disconnected.
            if (i % 2 == 1)
            {
                var again = await viewer.ConnectAsync();
                await again.InvokeAsync("Snapshot", NewSnapshot(id, lastSequence: 2 * batches), Ct);
            }
        }));

        await Wait.Until(() => ids.Where((_, i) => i % 2 == 0).All(id => Session(id).Status == SessionStatus.Disconnected));
        for (var i = 0; i < sessionCount; i++)
        {
            var session = Session(ids[i]);
            if (i % 2 == 1)
            {
                Assert.Equal(SessionStatus.Connected, session.Status);
                Assert.Equal(Enumerable.Range(1, 2 * batches).Select(n => (long)n), session.Actions.Select(a => a.Sequence));
                Assert.Equal(2 * batches, session.LastSequence);
            }
            else
            {
                // Cleared after its last batch, while still connected.
                Assert.Empty(session.History);
                Assert.Equal(2 * batches, session.LastSequence);
                Assert.Equal($$"""{"Count":{{2 * batches}}}""", session.Current["Counter"].StateJson);
            }
        }
    }
}
