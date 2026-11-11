using System.Threading.Channels;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Reports one store to the Viewer as a Session: tracks Feature state references per Action, connects in the
/// background, sends a Snapshot once connected and then each recorded Action. Never throws into the Host app.
/// </summary>
internal sealed class SessionReporter(FluxorViewerOptions options, IViewerConnectionFactory connectionFactory, ILogger logger)
    : IAsyncDisposable
{
    internal const int MaxBatchSize = 100;

    private readonly CancellationTokenSource stopping = new();

    // Full means "the Viewer can't keep up": AfterDispatch then stops queueing rather than waiting (see Record).
    private readonly Channel<PendingAction> queue = Channel.CreateBounded<PendingAction>(
        new BoundedChannelOptions(options.BufferSize) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    // Guards the tracked state below. AfterDispatch calls are already serialised by Fluxor; the lock is for the
    // sender, which copies the tracked state for a Snapshot from another thread.
    private readonly Lock gate = new();
    private IFeature[] features = [];
    private object?[] states = [];
    private long lastSequence;
    private bool queueing;
    private bool overflowed;
    private long droppedCount;

    private Task run = Task.CompletedTask;
    private IViewerConnection? connection;
    private int disposed;

    public SessionInfo? Session { get; private set; }

    /// <summary>Starts the Session. Returns without waiting for the connection.</summary>
    public void Start(IStore store)
    {
        Session = new SessionInfo(
            Guid.NewGuid(),
            options.AppName,
            OperatingSystem.IsBrowser() ? Runtime.WebAssembly : Runtime.Server,
            DateTimeOffset.UtcNow);

        lock (gate)
        {
            features = store.Features.Values.ToArray();
            states = features.Select(f => f.GetState()).ToArray();
        }

        run = RunAsync(Session, stopping.Token);
    }

    /// <summary>
    /// Called after reducers have run: assigns the next Sequence, finds the Changed Features by reference and,
    /// while connected, queues the Action. Doesn't serialise or wait; if the queue is full the Action is dropped
    /// and counted, and the sender follows up with a Snapshot so the Viewer records a Resync.
    /// </summary>
    public void Record(object action)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            lock (gate)
            {
                // Advance first: if anything below throws, the Viewer sees an honest gap.
                var sequence = ++lastSequence;

                List<TrackedState>? changed = null;
                for (var i = 0; i < features.Length; i++)
                {
                    var state = features[i].GetState();
                    if (ReferenceEquals(state, states[i]))
                        continue;
                    states[i] = state;
                    (changed ??= []).Add(new TrackedState(features[i], state));
                }

                if (queueing && !queue.Writer.TryWrite(new PendingAction(sequence, timestamp, action, changed ?? [])))
                {
                    // Overflow: stop queueing until the sender has drained the queue and sent a fresh Snapshot.
                    queueing = false;
                    overflowed = true;
                }

                if (overflowed)
                    droppedCount++;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: could not record {ActionType}.", action.GetType());
        }
    }

    private async Task RunAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        try
        {
            connection = connectionFactory.Create(new Uri(options.ServerUrl));
            await connection.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: could not connect to the Viewer at {ServerUrl}.", options.ServerUrl);
            return;
        }

        try
        {
            if (!await TrySendSnapshotAsync(connection, session, cancellationToken))
                return;

            var batch = new List<ActionRecorded>(MaxBatchSize);
            while (await queue.Reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                for (var read = 0; read < MaxBatchSize && queue.Reader.TryRead(out var pending); read++)
                {
                    if (TrySerialise(session, pending) is { } message)
                        batch.Add(message);
                }

                if (batch.Count > 0)
                    await connection.SendActionsAsync(batch.ToArray(), cancellationToken);

                // Nothing is queued while overflowed, so once drained, resync from a fresh Snapshot.
                if (queue.Reader.Count == 0 && IsOverflowed() && !await TrySendSnapshotAsync(connection, session, cancellationToken))
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // MW-6 adds reconnecting; until then the Session stops reporting.
            Disconnect();
            logger.LogDebug(ex, "FluxorViewer: lost the connection to the Viewer for Session {SessionId}.", session.SessionId);
        }
    }

    /// <summary>Captures and sends a Snapshot; false if it couldn't be serialised (the Session then stops).</summary>
    private async Task<bool> TrySendSnapshotAsync(IViewerConnection connection, SessionInfo session, CancellationToken cancellationToken)
    {
        Snapshot snapshot;
        try
        {
            snapshot = CaptureSnapshot(session);
        }
        catch (Exception ex)
        {
            Disconnect();
            logger.LogWarning(ex, "FluxorViewer: could not serialise the Snapshot for Session {SessionId}.", session.SessionId);
            return false;
        }

        await connection.SendSnapshotAsync(snapshot, cancellationToken);
        return true;
    }

    /// <summary>
    /// Copies the tracked states, Sequence and drop count together and (re)starts queueing, so the Snapshot matches
    /// the Actions that follow it exactly. Serialises outside the lock; the states are immutable.
    /// </summary>
    private Snapshot CaptureSnapshot(SessionInfo session)
    {
        long sequence, dropped;
        TrackedState[] tracked;
        lock (gate)
        {
            sequence = lastSequence;
            dropped = droppedCount;
            tracked = features.Select((f, i) => new TrackedState(f, states[i])).ToArray();
            droppedCount = 0;
            overflowed = false;
            queueing = true;
        }

        return Messages.Snapshot(session, sequence, dropped, tracked);
    }

    private bool IsOverflowed()
    {
        lock (gate)
            return overflowed;
    }

    private ActionRecorded? TrySerialise(SessionInfo session, PendingAction pending)
    {
        try
        {
            return Messages.ActionRecorded(session, pending);
        }
        catch (Exception ex)
        {
            // MW-4 replaces this with a {"$error": …} placeholder.
            logger.LogWarning(ex, "FluxorViewer: could not serialise Action {Sequence} ({ActionType}); skipped.",
                pending.Sequence, pending.Action.GetType());
            return null;
        }
    }

    private void Disconnect()
    {
        lock (gate)
            queueing = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        Disconnect();
        queue.Writer.TryComplete();
        await stopping.CancelAsync();
        try
        {
            await run;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: Session ended with an error.");
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "FluxorViewer: error closing the connection to the Viewer.");
            }
        }

        stopping.Dispose();
    }
}
