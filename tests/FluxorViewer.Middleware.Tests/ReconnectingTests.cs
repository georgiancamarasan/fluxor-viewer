using System.Text.Json;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-6: buffer while offline, reconnect, and resync (ADR 0005).
public class ReconnectingTests
{
    [Fact]
    public async Task Actions_from_before_the_Viewer_is_up_arrive_after_a_Snapshot_of_the_state_before_them()
    {
        await using var harness = new Harness(bufferSize: 10);
        var connection = await harness.Start(); // Viewer down: the connect waits.

        for (var i = 0; i < 19; i++) // StoreInitializedAction is 1, increments 2..20; 1..10 are dropped.
            harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Viewer.ConnectGate.SetResult();
        await Harness.ActionsSent(connection, 10);

        var snapshot = Assert.IsType<Snapshot>(connection.Sent.First());
        Assert.Equal(10, snapshot.LastSequence);
        Assert.Equal(10, snapshot.DroppedCount);
        Assert.Equal(9, Count(snapshot));
        Assert.Equal(Enumerable.Range(11, 10).Select(i => (long)i), connection.Actions.Select(a => a.Sequence));
        Assert.Equal(Enumerable.Range(10, 10), connection.Actions.Select(a => CountIn(a.ChangedFeatures)));
    }

    [Fact]
    public async Task After_a_failed_send_the_Session_reconnects_and_resyncs_without_resending()
    {
        await using var harness = new Harness();
        var (first, start) = await harness.StartConnected();
        harness.Viewer.ActionsFailure = new HttpRequestException("Gone");

        harness.Dispatcher.Dispatch(new IncrementAction()); // 2: its send fails, so it counts as delivered.
        await first.Disposed.WaitAsync(Wait.Timeout, Wait.Ct);
        harness.Viewer.ActionsFailure = null;
        harness.Dispatcher.Dispatch(new IncrementAction()); // 3: buffered while offline.
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);
        var second = harness.Viewer.Connections.Last();
        await Harness.ActionsSent(second, 1);

        var resync = Assert.IsType<Snapshot>(second.Sent.First());
        Assert.Equal(start.SessionId, resync.SessionId);
        Assert.Equal(2, resync.LastSequence);
        Assert.Equal(0, resync.DroppedCount);
        Assert.Equal(1, Count(resync));
        Assert.Equal([3L], second.Actions.Select(a => a.Sequence));
        Assert.All(harness.Logs.Entries.Where(e => e.Exception is HttpRequestException), e => Assert.Equal(LogLevel.Debug, e.Level));
    }

    [Fact]
    public async Task Connection_attempts_back_off_to_every_10_seconds_and_start_again_at_1_after_connecting()
    {
        await using var harness = new Harness(viewer: new FakeViewer { ConnectFailure = new HttpRequestException("Refused") });
        await harness.Start();
        var started = harness.Time.GetUtcNow();
        var attempts = new List<DateTimeOffset> { started };

        async Task NextAttempt()
        {
            var count = harness.Viewer.Connections.Count;
            await harness.AdvanceUntil(() => harness.Viewer.Connections.Count > count);
            attempts.Add(harness.Time.GetUtcNow());
        }

        for (var i = 0; i < 5; i++)
            await NextAttempt();
        harness.Viewer.ConnectFailure = null;
        await NextAttempt();
        var connected = harness.Viewer.Connections.Last();
        await Wait.Until(() => connected.Actions.Count == 1);
        harness.Viewer.ActionsFailure = new HttpRequestException("Gone");
        harness.Dispatcher.Dispatch(new IncrementAction());
        await NextAttempt();

        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => Math.Round((b - a).TotalSeconds));
        Assert.Equal([1d, 2d, 4d, 10d, 10d, 10d, 1d], gaps);
    }

    [Fact]
    public async Task A_rejection_by_the_Viewer_stops_the_Session_for_good()
    {
        var viewer = new FakeViewer { SnapshotFailure = new Microsoft.AspNetCore.SignalR.HubException(
            "An unexpected error occurred invoking 'Snapshot' on the server. HubException: FluxorViewer: wrong protocol version") };
        await using var harness = new Harness(viewer: viewer);
        await harness.Start();
        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Level == LogLevel.Warning));

        viewer.SnapshotFailure = null;
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Dispatcher.Dispatch(new IncrementAction());
        await Task.Delay(100, Wait.Ct);

        Assert.Single(viewer.Connections);
    }

    [Fact]
    public async Task An_unexpected_error_in_the_Viewer_is_retried_like_a_lost_connection()
    {
        // What SignalR sends for any exception a hub method throws, unless detailed errors are on.
        var viewer = new FakeViewer
        {
            SnapshotFailure = new Microsoft.AspNetCore.SignalR.HubException(
                "An unexpected error occurred invoking 'Snapshot' on the server."),
        };
        await using var harness = new Harness(viewer: viewer);
        var first = await harness.Start();
        await first.Disposed.WaitAsync(Wait.Timeout, Wait.Ct);

        viewer.SnapshotFailure = null;
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);

        Assert.IsType<Snapshot>(await harness.Viewer.Connections.Last().FirstMessage.WaitAsync(Wait.Timeout, Wait.Ct));
        Assert.DoesNotContain(harness.Logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Connecting_without_the_Snapshot_being_accepted_keeps_backing_off()
    {
        var viewer = new FakeViewer { SnapshotFailure = new HttpRequestException("Reset") };
        await using var harness = new Harness(viewer: viewer);
        await harness.Start();
        var attempts = new List<DateTimeOffset> { harness.Time.GetUtcNow() };

        for (var i = 0; i < 4; i++)
        {
            var count = harness.Viewer.Connections.Count;
            await harness.AdvanceUntil(() => harness.Viewer.Connections.Count > count);
            attempts.Add(harness.Time.GetUtcNow());
        }

        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => Math.Round((b - a).TotalSeconds));
        Assert.Equal([1d, 2d, 4d, 10d], gaps);
    }

    [Fact]
    public async Task Drops_reported_by_a_Snapshot_that_failed_to_send_are_reported_again()
    {
        await using var harness = new Harness(bufferSize: 5);
        var connection = await harness.Start(); // Viewer down: the connect waits.
        for (var i = 0; i < 10; i++) // 1..11 into a buffer of 5: 1..6 dropped.
            harness.Dispatcher.Dispatch(new IncrementAction());
        harness.Viewer.SnapshotFailure = new HttpRequestException("Reset");

        harness.Viewer.ConnectGate.SetResult();
        await connection.Disposed.WaitAsync(Wait.Timeout, Wait.Ct);
        harness.Viewer.SnapshotFailure = null;
        harness.Dispatcher.Dispatch(new IncrementAction()); // 12: 7 dropped.
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);
        var second = harness.Viewer.Connections.Last();
        await Harness.ActionsSent(second, 5);

        var snapshot = Assert.IsType<Snapshot>(second.Sent.First());
        Assert.Equal(7, snapshot.DroppedCount);
        Assert.Equal(7, snapshot.LastSequence);
        Assert.Equal(Enumerable.Range(8, 5).Select(i => (long)i), second.Actions.Select(a => a.Sequence));
    }

    [Fact]
    public async Task An_idle_Session_reconnects_when_its_connection_closes()
    {
        await using var harness = new Harness();
        var (first, start) = await harness.StartConnected();

        first.Close(); // e.g. the Viewer restarted while the app was idle.
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);
        var second = harness.Viewer.Connections.Last();

        var resync = Assert.IsType<Snapshot>(await second.FirstMessage.WaitAsync(Wait.Timeout, Wait.Ct));
        Assert.Equal(start.SessionId, resync.SessionId);
        Assert.Equal(1, resync.LastSequence);
        await first.Disposed.WaitAsync(Wait.Timeout, Wait.Ct);
    }

    [Fact]
    public async Task Disposing_the_store_while_waiting_to_reconnect_stops_trying()
    {
        var viewer = new FakeViewer { ConnectFailure = new HttpRequestException("Refused") };
        var harness = new Harness(viewer: viewer);
        await harness.Start();
        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Exception is HttpRequestException));

        await harness.DisposeAsync().AsTask().WaitAsync(Wait.Timeout, Wait.Ct);
        viewer.ConnectFailure = null;
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100, Wait.Ct);

        Assert.Single(viewer.Connections);
    }

    private static int Count(Snapshot snapshot) => CountIn(snapshot.Features);

    private static int CountIn(IEnumerable<FeatureState> features) =>
        JsonDocument.Parse(features.Single(f => f.Name == "Counter").StateJson).RootElement.GetProperty("Count").GetInt32();
}
