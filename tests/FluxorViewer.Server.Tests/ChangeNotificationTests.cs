using System.Collections.Concurrent;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Server.Tests;

// IN-7: the registry tells the UI what changed, once per call, after the change is made.
public class ChangeNotificationTests
{
    private readonly CapturingLogger logger = new();
    private readonly SessionRegistry registry;
    private readonly List<SessionChange> changes = [];

    public ChangeNotificationTests()
    {
        registry = new SessionRegistry(logger);
        registry.Changed += changes.Add;
    }

    private static Snapshot NewSnapshot(Guid sessionId, long lastSequence = 0) => new(
        ProtocolVersion.Current,
        sessionId,
        "TestApp",
        Runtime.Server,
        DateTimeOffset.UtcNow,
        lastSequence,
        DroppedCount: 0,
        [new FeatureState("Counter", "App.CounterState", """{"Count":0}""")]);

    private static ActionRecorded[] Increments(Guid sessionId, long from, int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new ActionRecorded(
            sessionId, from + i, DateTimeOffset.UtcNow, "App.IncrementAction", "{}",
            [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{from + i}}}""")]))
    ];

    private Guid Start(string connectionId = "c1")
    {
        var sessionId = Guid.NewGuid();
        registry.SnapshotReceived(connectionId, NewSnapshot(sessionId));
        changes.Clear();
        return sessionId;
    }

    [Fact]
    public void A_new_Session_raises_SessionAdded_only()
    {
        var sessionId = Guid.NewGuid();

        registry.SnapshotReceived("c1", NewSnapshot(sessionId));

        Assert.Equal([new SessionAdded(sessionId)], changes);
    }

    [Fact]
    public void A_batch_of_Actions_raises_one_EntriesAppended_with_the_number_appended()
    {
        var sessionId = Start();

        registry.ActionsReceived("c1", Increments(sessionId, 1, 3));

        Assert.Equal([new EntriesAppended(sessionId, 3)], changes);
    }

    [Fact]
    public void An_empty_batch_raises_nothing()
    {
        Start();

        registry.ActionsReceived("c1", []);

        Assert.Empty(changes);
    }

    [Fact]
    public void Closing_and_reconnecting_raise_status_changes()
    {
        var sessionId = Start();

        registry.ConnectionClosed("c1");
        registry.SnapshotReceived("c2", NewSnapshot(sessionId));

        Assert.Equal(
            [new SessionStatusChanged(sessionId, SessionStatus.Disconnected), new SessionStatusChanged(sessionId, SessionStatus.Connected)],
            changes);
    }

    [Fact]
    public void Moving_a_connected_Session_to_a_new_connection_raises_nothing_and_neither_does_the_old_one()
    {
        var sessionId = Start("old");

        registry.SnapshotReceived("new", NewSnapshot(sessionId));
        registry.ActionsReceived("old", Increments(sessionId, 1, 2));
        registry.ConnectionClosed("old");

        Assert.Empty(changes);
    }

    [Fact]
    public void Clearing_a_connected_Session_raises_SessionCleared_and_keeps_it()
    {
        var sessionId = Start();

        registry.Clear(sessionId);

        Assert.Equal([new SessionCleared(sessionId)], changes);
        Assert.NotNull(registry.Find(sessionId));
    }

    [Fact]
    public void Clearing_a_disconnected_Session_raises_SessionCleared_and_removes_it()
    {
        var sessionId = Start();
        registry.ConnectionClosed("c1");
        changes.Clear();

        registry.Clear(sessionId);

        Assert.Equal([new SessionCleared(sessionId)], changes);
        Assert.Null(registry.Find(sessionId));
    }

    [Fact]
    public void Clearing_an_unknown_Session_raises_nothing()
    {
        registry.Clear(Guid.NewGuid());

        Assert.Empty(changes);
    }

    [Fact]
    public void A_throwing_subscriber_affects_neither_ingestion_nor_other_subscribers()
    {
        var sessionId = Start();
        var others = new List<SessionChange>();
        registry.Changed += _ => throw new InvalidOperationException("boom");
        registry.Changed += others.Add;

        registry.ActionsReceived("c1", Increments(sessionId, 1, 2));

        Assert.Equal([new EntriesAppended(sessionId, 2)], others);
        Assert.Equal(2, registry.Find(sessionId)!.Actions.Count());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException { Message: "boom" });
    }

    [Fact]
    public void Subscribers_run_after_the_change_is_made_and_outside_the_lock()
    {
        var sessionId = Start();
        int? seenOnAnotherThread = null;
        registry.Changed += _ =>
        {
            // If the lock were still held, a reader on another thread would block until the timeout.
            var read = Task.Run(() => registry.Find(sessionId));
            if (read.Wait(TimeSpan.FromSeconds(2)))
                seenOnAnotherThread = read.Result!.Actions.Count();
        };

        registry.ActionsReceived("c1", Increments(sessionId, 1, 1));

        Assert.Equal(1, seenOnAnotherThread);
    }

    private sealed class CapturingLogger : ILogger<SessionRegistry>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, exception));
    }
}

// IN-7 through the real hub: the registry the UI injects raises changes for what the middleware sends.
public class HubChangeNotificationTests(Viewer viewer) : IClassFixture<Viewer>
{
    [Fact]
    public async Task Snapshot_Actions_and_close_reach_subscribers()
    {
        var sessionId = Guid.NewGuid();
        var changes = new ConcurrentQueue<SessionChange>();
        void Record(SessionChange change)
        {
            if (change.SessionId == sessionId)
                changes.Enqueue(change);
        }
        viewer.Registry.Changed += Record;
        try
        {
            var connection = await viewer.ConnectAsync();
            await connection.InvokeAsync("Snapshot", new Snapshot(
                ProtocolVersion.Current, sessionId, "TestApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
                [new FeatureState("Counter", "App.CounterState", """{"Count":0}""")]), Wait.Ct);
            await connection.InvokeAsync("Actions", new[]
            {
                new ActionRecorded(sessionId, 1, DateTimeOffset.UtcNow, "App.IncrementAction", "{}",
                    [new FeatureState("Counter", "App.CounterState", """{"Count":1}""")]),
            }, Wait.Ct);
            await connection.DisposeAsync();

            await Wait.Until(() => changes.Count == 3);
            Assert.Equal(
                [new SessionAdded(sessionId), new EntriesAppended(sessionId, 1), new SessionStatusChanged(sessionId, SessionStatus.Disconnected)],
                changes);
        }
        finally
        {
            viewer.Registry.Changed -= Record;
        }
    }
}
