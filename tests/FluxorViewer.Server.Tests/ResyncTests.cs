using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Server.Tests;

// IN-5: a Snapshot that doesn't follow on from what the Viewer already had marks a Resync in the History.
public class ResyncTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static Snapshot NewSnapshot(Guid sessionId, long lastSequence = 0, long droppedCount = 0, int count = 0) => new(
        ProtocolVersion.Current,
        sessionId,
        "TestApp",
        Runtime.Server,
        DateTimeOffset.UtcNow,
        lastSequence,
        droppedCount,
        [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{count}}}""")]);

    private static ActionRecorded[] Increments(Guid sessionId, long from, long to) =>
    [
        .. Enumerable.Range((int)from, (int)(to - from + 1)).Select(i => new ActionRecorded(
            sessionId, i, DateTimeOffset.UtcNow, "App.IncrementAction", "{}",
            [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{i}}}""")]))
    ];

    private async Task<HubConnection> SnapshotAsync(Snapshot snapshot)
    {
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", snapshot, Ct);
        return connection;
    }

    private ReceivedSession Session(Guid sessionId) => viewer.Registry.Find(sessionId)!;

    [Fact]
    public async Task A_reconnect_after_missing_Actions_marks_a_Resync_with_the_Sequence_gap()
    {
        var sessionId = Guid.NewGuid();
        await using (var first = await SnapshotAsync(NewSnapshot(sessionId)))
            await first.InvokeAsync("Actions", Increments(sessionId, 1, 3), Ct);

        await using var second = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 9, count: 9));

        var resync = Assert.IsType<ResyncEntry>(Session(sessionId).History[^1]);
        Assert.Equal(9, resync.Sequence);
        Assert.Equal(6, resync.Lost);
        Assert.Equal("""{"Count":9}""", resync.After["Counter"].StateJson);
    }

    [Fact]
    public async Task A_reconnect_that_missed_nothing_marks_no_Resync()
    {
        var sessionId = Guid.NewGuid();
        await using (var first = await SnapshotAsync(NewSnapshot(sessionId)))
            await first.InvokeAsync("Actions", Increments(sessionId, 1, 3), Ct);

        await using var second = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 3, count: 3));

        Assert.All(Session(sessionId).History, e => Assert.IsType<ActionEntry>(e));
    }

    [Fact]
    public async Task Dropped_Actions_count_as_lost_even_when_the_Sequence_gap_is_smaller()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Ct);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 5, droppedCount: 4, count: 5), Ct);

        Assert.Equal(4, Assert.IsType<ResyncEntry>(Session(sessionId).History[^1]).Lost);
    }

    [Fact]
    public async Task A_new_Session_whose_first_Snapshot_dropped_Actions_starts_with_a_Resync_for_them()
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 40, droppedCount: 12, count: 40));

        var resync = Assert.IsType<ResyncEntry>(Assert.Single(Session(sessionId).History));
        Assert.Equal(12, resync.Lost);
        Assert.Equal(40, resync.Sequence);
    }

    [Fact]
    public async Task A_new_Session_that_dropped_nothing_starts_with_an_empty_History_whatever_its_LastSequence()
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 40, count: 40));

        Assert.Empty(Session(sessionId).History);
    }

    [Fact]
    public async Task A_connected_Session_that_was_Cleared_still_marks_a_Resync_for_Actions_lost_after()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Ct);
        viewer.Registry.Clear(sessionId);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 8, droppedCount: 5, count: 8), Ct);

        Assert.Equal(5, Assert.IsType<ResyncEntry>(Assert.Single(Session(sessionId).History)).Lost);
    }

    [Fact]
    public async Task A_Snapshot_older_than_the_last_Action_is_a_Resync_without_a_count_and_never_rewinds()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 5), Ct);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 3, count: 3), Ct);
        await connection.InvokeAsync("Actions", Increments(sessionId, 4, 4), Ct);

        var session = Session(sessionId);
        Assert.Equal([1, 2, 3, 4, 5], session.Actions.Take(5).Select(a => a.Sequence));
        var resync = Assert.IsType<ResyncEntry>(session.History[5]);
        Assert.Null(resync.Lost);
        Assert.Equal(3, resync.Sequence);
        Assert.Equal(4, Assert.IsType<ActionEntry>(session.History[6]).Sequence); // after the Snapshot, 4 is no duplicate
        Assert.Equal(4, session.LastSequence);
    }

    [Fact]
    public async Task Older_Snapshots_are_logged_as_a_Warning_once_per_Session()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 5), Ct);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 3, count: 3), Ct);
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 1, count: 1), Ct);

        Assert.Single(viewer.Logs, l =>
            l.Level == LogLevel.Warning && l.Category == typeof(SessionRegistry).FullName && l.Message.Contains(sessionId.ToString()));
    }

    [Fact]
    public async Task Actions_at_or_before_the_last_received_Sequence_are_ignored()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Ct);

        await connection.InvokeAsync("Actions", Increments(sessionId, 2, 4), Ct);
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 9, count: 9), Ct);
        await connection.InvokeAsync("Actions", Increments(sessionId, 9, 10), Ct);

        var session = Session(sessionId);
        Assert.Equal([1, 2, 3, 4, 10], session.Actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":3}""", session.Actions.ElementAt(3).Before["Counter"].StateJson);
    }

    [Fact]
    public async Task A_Resync_raises_EntriesAppended_along_with_any_other_change()
    {
        var sessionId = Guid.NewGuid();
        var changes = new List<SessionChange>();
        void Record(SessionChange c)
        {
            if (c.SessionId == sessionId)
                lock (changes) changes.Add(c);
        }
        viewer.Registry.Changed += Record;
        await using (var first = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 2, droppedCount: 2, count: 2)))
            await first.InvokeAsync("Actions", Increments(sessionId, 3, 3), Ct);
        await Wait.Until(() => viewer.Registry.Find(sessionId)!.Status == SessionStatus.Disconnected);
        await using var second = await SnapshotAsync(NewSnapshot(sessionId, lastSequence: 7, count: 7));
        await second.InvokeAsync("Snapshot", NewSnapshot(sessionId, lastSequence: 9, droppedCount: 2, count: 9), Ct);
        viewer.Registry.Changed -= Record;

        Assert.Equal(
        [
            new SessionAdded(sessionId), new EntriesAppended(sessionId, 1),
            new EntriesAppended(sessionId, 1),
            new SessionStatusChanged(sessionId, SessionStatus.Disconnected),
            new SessionStatusChanged(sessionId, SessionStatus.Connected), new EntriesAppended(sessionId, 1),
            new EntriesAppended(sessionId, 1),
        ], changes);
    }
}
