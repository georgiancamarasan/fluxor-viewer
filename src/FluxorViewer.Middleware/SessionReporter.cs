using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Reports one store to the Viewer as a Session: tracks Feature state references per Action, connects in the
/// background, buffers recorded Actions and sends them after a Snapshot of the state before them. Never throws into the Host app.
/// </summary>
internal sealed class SessionReporter(
    FluxorViewerOptions options, IViewerConnectionFactory connectionFactory, ILogger logger, TimeProvider time)
    : IAsyncDisposable
{
    internal const int MaxBatchSize = 100;

    private readonly CancellationTokenSource stopping = new();
    private readonly ViewerJson json = new(options.MaxDepth, logger);

    // Released when there may be something to send; the sender waits on it when the buffer is empty. Uncapped,
    // since Record may release it again before the sender has waited: that only costs a spurious wake-up.
    private readonly SemaphoreSlim wake = new(0);

    // Guards everything below. AfterDispatch calls are already serialised by Fluxor; the lock is for the sender,
    // which takes from the buffer and copies the base from another thread.
    private readonly Lock gate = new();
    private IFeature[] features = [];
    private Dictionary<IFeature, int> featureIndex = [];
    private object?[] states = [];
    private long lastSequence;

    // Every Action not yet sent, oldest first, and the base: the states and Sequence just after the last Action
    // that left the buffer (sent or dropped). Snapshots are taken from the base (ADR 0005).
    private readonly Queue<PendingAction> buffer = new();
    private object?[] baseStates = [];
    private long baseSequence;
    private long droppedCount;
    private bool snapshotDue = true;
    private bool signalled;
    private bool stopped;

    // Connection attempts since the Viewer last accepted a Snapshot; picks the backoff. Only the sender uses it.
    private int failedAttempts;

    private Task run = Task.CompletedTask;
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
            featureIndex = features.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i);
            states = features.Select(f => f.GetState()).ToArray();
            baseStates = (object?[])states.Clone();
        }

        run = RunAsync(Session, stopping.Token);
    }

    /// <summary>
    /// Called after reducers have run: assigns the next Sequence, finds the Changed Features by reference and
    /// appends the Action to the buffer. Doesn't serialise or wait; if the buffer is full its oldest Action is
    /// dropped into the base and counted, and the sender follows up with a Snapshot so the Viewer records a Resync.
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

                if (stopped)
                    return;

                if (buffer.Count >= options.BufferSize)
                {
                    MoveBasePast(buffer.Dequeue());
                    droppedCount++;
                    snapshotDue = true;
                }

                buffer.Enqueue(new PendingAction(sequence, timestamp, action, changed ?? []));
                Wake();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: could not record {ActionType}.", action.GetType());
        }
    }

    /// <summary>
    /// Connects, sends, and on any failure waits for the backoff and starts over with a new connection, until the
    /// Session ends or the Viewer rejects it.
    /// </summary>
    private async Task RunAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        while (true)
        {
            var connection = connectionFactory.Create(new Uri(options.ServerUrl));
            try
            {
                await connection.StartAsync(cancellationToken);
                lock (gate)
                    snapshotDue = true;
                if (!await SendAsync(connection, session, cancellationToken))
                    return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HubException ex) when (Rejection.IsRejection(ex.Message))
            {
                // The Viewer refused us (e.g. protocol version mismatch): retrying won't help, and the developer needs to
                // see why. Any other HubException is an unexpected error in the Viewer, retried like a lost connection.
                Stop();
                logger.LogWarning("FluxorViewer: the Viewer rejected Session {SessionId}: {Reason}", session.SessionId, Rejection.Reason(ex.Message));
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "FluxorViewer: lost or couldn't open the connection to the Viewer at {ServerUrl} for Session {SessionId}.",
                    options.ServerUrl, session.SessionId);
            }
            finally
            {
                await DisposeQuietlyAsync(connection);
            }

            try
            {
                await Task.Delay(Backoff[Math.Min(failedAttempts++, Backoff.Length - 1)], time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Delays between connection attempts; the last repeats.</summary>
    internal static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// Sends a Snapshot when due and the buffered Actions until the connection fails (throws) or the Session is
    /// cancelled; false if a Snapshot couldn't be built, which stops the Session.
    /// </summary>
    private async Task<bool> SendAsync(IViewerConnection connection, SessionInfo session, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Check again after each Snapshot: Actions may have been dropped meanwhile, making another one due.
            if (TakeSnapshot() is { } snapshot)
            {
                if (!await TrySendSnapshotAsync(connection, session, snapshot, cancellationToken))
                    return false;
                continue;
            }

            var batch = TakeBatch();
            if (batch.Count == 0)
            {
                await WaitForWorkAsync(connection, cancellationToken);
                continue;
            }

            var messages = batch.Select(p => TrySerialise(session, p)).OfType<ActionRecorded>().ToArray();
            if (messages.Length > 0)
                await connection.SendActionsAsync(messages, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until there may be something to send; throws if the connection closes first, so an idle Session
    /// reconnects without waiting for the next Action.
    /// </summary>
    private async Task WaitForWorkAsync(IViewerConnection connection, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var woken = wake.WaitAsync(waiting.Token);
        if (await Task.WhenAny(woken, connection.Closed) == woken)
        {
            await woken;
            return;
        }

        await waiting.CancelAsync(); // Don't leave a waiter behind to swallow a later wake-up.
        throw new IOException("The Viewer closed the connection.");
    }

    private async Task DisposeQuietlyAsync(IViewerConnection connection)
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

    /// <summary>A Snapshot of the base, if one is due (on connecting, or after a drop).</summary>
    private DueSnapshot? TakeSnapshot()
    {
        lock (gate)
        {
            if (!snapshotDue)
                return null;
            snapshotDue = false;
            return new DueSnapshot(
                baseSequence, droppedCount, features.Select((f, i) => new TrackedState(f, baseStates[i])).ToArray());
        }
    }

    /// <summary>
    /// Takes up to <see cref="MaxBatchSize"/> buffered Actions. They count as delivered from here on: the base moves
    /// past them, so a later Snapshot never rewinds below them.
    /// </summary>
    private List<PendingAction> TakeBatch()
    {
        lock (gate)
        {
            signalled = false;
            var batch = new List<PendingAction>(Math.Min(buffer.Count, MaxBatchSize));
            while (batch.Count < MaxBatchSize && buffer.TryDequeue(out var pending))
            {
                MoveBasePast(pending);
                batch.Add(pending);
            }

            return batch;
        }
    }

    private void MoveBasePast(PendingAction pending)
    {
        foreach (var changed in pending.ChangedFeatures)
            baseStates[featureIndex[changed.Feature]] = changed.State;
        baseSequence = pending.Sequence;
    }

    private void Wake()
    {
        if (signalled)
            return;
        signalled = true;
        wake.Release();
    }

    /// <summary>Builds and sends a Snapshot; false if it couldn't be built (the Session then stops).</summary>
    private async Task<bool> TrySendSnapshotAsync(
        IViewerConnection connection, SessionInfo session, DueSnapshot due, CancellationToken cancellationToken)
    {
        Snapshot snapshot;
        try
        {
            snapshot = Messages.Snapshot(session, due.LastSequence, due.DroppedCount, due.Features, json);
        }
        catch (Exception ex)
        {
            Stop();
            logger.LogWarning(ex, "FluxorViewer: could not build the Snapshot for Session {SessionId}.", session.SessionId);
            return false;
        }

        await connection.SendSnapshotAsync(snapshot, cancellationToken);
        failedAttempts = 0; // Accepted: the next failure starts the backoff over.
        lock (gate)
            droppedCount -= due.DroppedCount;
        return true;
    }

    private ActionRecorded? TrySerialise(SessionInfo session, PendingAction pending)
    {
        try
        {
            return Messages.ActionRecorded(session, pending, json);
        }
        catch (Exception ex)
        {
            // A value that won't serialise is already a Serialisation error; this is anything else, e.g. a Feature's name.
            logger.LogWarning(ex, "FluxorViewer: could not build Action {Sequence} ({ActionType}); skipped.",
                pending.Sequence, pending.Action.GetType());
            return null;
        }
    }

    /// <summary>A Snapshot of the base, copied under the lock and serialised outside it (the states are immutable).</summary>
    private sealed record DueSnapshot(long LastSequence, long DroppedCount, TrackedState[] Features);

    /// <summary>Stops buffering for good: nothing more will be sent.</summary>
    private void Stop()
    {
        lock (gate)
        {
            stopped = true;
            buffer.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        Stop();
        await stopping.CancelAsync();
        try
        {
            await run;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: Session ended with an error.");
        }

        stopping.Dispose();
        wake.Dispose();
    }
}
