using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-5: a Snapshot that doesn't follow on from what the Viewer already had marks a Resync in the History.
public class ResyncTests(Viewer viewer) : IClassFixture<Viewer>
{
    private async Task<HubConnection> SnapshotAsync(Snapshot snapshot)
    {
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);
        return connection;
    }

    private ReceivedSession Session(Guid sessionId) => viewer.Registry.Find(sessionId)!;

    [Fact]
    public async Task A_Snapshot_after_missed_Actions_marks_a_Resync_for_the_Sequences_missed()
    {
        var sessionId = Guid.NewGuid();
        await using (var first = await SnapshotAsync(NewSnapshot(sessionId)))
            await first.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);

        await using var second = await SnapshotAsync(SnapshotAfter(sessionId, 9));

        var resync = Assert.IsType<ResyncEntry>(Session(sessionId).History[^1]);
        Assert.Equal(9, resync.Sequence);
        Assert.Equal(6, resync.Lost);
        Assert.Equal("""{"Count":9}""", resync.After["Counter"].StateJson);
    }

    [Fact]
    public async Task A_Snapshot_that_follows_on_from_the_last_Action_marks_no_Resync()
    {
        var sessionId = Guid.NewGuid();
        await using (var first = await SnapshotAsync(NewSnapshot(sessionId)))
            await first.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);

        await using var second = await SnapshotAsync(SnapshotAfter(sessionId, 3));

        Assert.All(Session(sessionId).History, e => Assert.IsType<ActionEntry>(e));
    }

    [Fact]
    public async Task Dropped_Actions_count_as_lost_even_when_fewer_Sequences_were_missed()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);

        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 5) with { DroppedCount = 4 }, Wait.CancellationToken);

        Assert.Equal(4, Assert.IsType<ResyncEntry>(Session(sessionId).History[^1]).Lost);
    }

    [Fact]
    public async Task A_new_Session_whose_first_Snapshot_dropped_Actions_starts_with_a_Resync_for_them()
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await SnapshotAsync(SnapshotAfter(sessionId, 40) with { DroppedCount = 12 });

        var resync = Assert.IsType<ResyncEntry>(Assert.Single(Session(sessionId).History));
        Assert.Equal(12, resync.Lost);
        Assert.Equal(40, resync.Sequence);
    }

    [Fact]
    public async Task A_new_Session_that_dropped_nothing_starts_with_an_empty_History_whatever_its_LastSequence()
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await SnapshotAsync(SnapshotAfter(sessionId, 40));

        Assert.Empty(Session(sessionId).History);
    }

    [Fact]
    public async Task A_connected_Session_that_was_Cleared_still_marks_a_Resync_for_Actions_lost_after()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);
        viewer.Registry.Clear(sessionId);

        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 8) with { DroppedCount = 5 }, Wait.CancellationToken);

        Assert.Equal(5, Assert.IsType<ResyncEntry>(Assert.Single(Session(sessionId).History)).Lost);
    }

    [Fact]
    public async Task A_Snapshot_older_than_the_last_Action_is_a_Resync_without_a_count_and_never_rewinds()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 5), Wait.CancellationToken);

        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 3), Wait.CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], Session(sessionId).Actions.Select(a => a.Sequence));
        var resync = Assert.IsType<ResyncEntry>(Session(sessionId).History[^1]);
        Assert.Null(resync.Lost);
        Assert.Equal(3, resync.Sequence);

        await connection.InvokeAsync("Actions", Increments(sessionId, 4, 4), Wait.CancellationToken);
        Assert.Equal(4, Assert.IsType<ActionEntry>(Session(sessionId).History[^1]).Sequence); // after the Snapshot, 4 is no duplicate
        Assert.Equal(4, Session(sessionId).LastSequence);
    }

    private IEnumerable<string> OlderSnapshotWarnings(Guid sessionId) => viewer.Logs
        .Where(l => l.Level == LogLevel.Warning && l.Category == "FluxorViewer.Server.Sessions.SessionRegistry" && l.Message.Contains(sessionId.ToString()))
        .Select(l => l.Message);

    [Fact]
    public async Task Older_Snapshots_are_logged_as_a_Warning_once_per_Session()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 5), Wait.CancellationToken);

        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 3), Wait.CancellationToken);
        Assert.Single(OlderSnapshotWarnings(sessionId));

        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 1), Wait.CancellationToken);
        Assert.Single(OlderSnapshotWarnings(sessionId));
        Assert.Equal([1, 2, 3, 4, 5], Session(sessionId).Actions.Select(a => a.Sequence)); // nothing rewound
    }

    [Fact]
    public async Task Actions_at_or_before_the_last_received_Action_are_ignored()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);

        await connection.InvokeAsync("Actions", Increments(sessionId, 2, 4), Wait.CancellationToken);

        var session = Session(sessionId);
        Assert.Equal([1, 2, 3, 4], session.Actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":3}""", session.Actions.ElementAt(3).Before["Counter"].StateJson);
    }

    [Fact]
    public async Task Actions_at_or_before_the_last_Snapshot_are_ignored()
    {
        var sessionId = Guid.NewGuid();
        await using var connection = await SnapshotAsync(NewSnapshot(sessionId));
        await connection.InvokeAsync("Actions", Increments(sessionId, 1, 3), Wait.CancellationToken);
        await connection.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 9), Wait.CancellationToken);

        await connection.InvokeAsync("Actions", Increments(sessionId, 9, 10), Wait.CancellationToken);

        Assert.Equal([1, 2, 3, 10], Session(sessionId).Actions.Select(a => a.Sequence));
    }

    [Fact]
    public async Task A_Resync_raises_EntriesAppended_along_with_any_other_change()
    {
        var sessionId = Guid.NewGuid();
        using var recorder = new ChangeRecorder(viewer.Registry, sessionId);

        var first = await SnapshotAsync(SnapshotAfter(sessionId, 2) with { DroppedCount = 2 });
        Assert.Equal([new SessionAdded(sessionId), new EntriesAppended(sessionId, 1)], recorder.Changes);

        await first.DisposeAsync();
        await Wait.Until(() => recorder.Changes.Count == 3);
        Assert.Equal(new SessionStatusChanged(sessionId, SessionStatus.Disconnected), recorder.Changes[^1]);

        await using var second = await SnapshotAsync(SnapshotAfter(sessionId, 7));
        Assert.Equal([new SessionStatusChanged(sessionId, SessionStatus.Connected), new EntriesAppended(sessionId, 1)], recorder.Changes.Skip(3));

        await second.InvokeAsync("Snapshot", SnapshotAfter(sessionId, 9) with { DroppedCount = 2 }, Wait.CancellationToken);
        Assert.Equal(new EntriesAppended(sessionId, 1), recorder.Changes[^1]);
        Assert.Equal(6, recorder.Changes.Count);
    }
}
