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
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<PendingAction> queue = Channel.CreateUnbounded<PendingAction>(new() { SingleReader = true });

    // Guards the tracked state below. AfterDispatch calls are already serialised by Fluxor; the lock is for the
    // sender, which copies the tracked state for a Snapshot from another thread.
    private readonly Lock gate = new();
    private IFeature[] features = [];
    private object?[] states = [];
    private long lastSequence;
    private bool connected;

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
    /// while connected, queues the Action. Doesn't serialise.
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

                if (connected)
                    queue.Writer.TryWrite(new PendingAction(sequence, timestamp, action, changed ?? []));
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

        Snapshot snapshot;
        try
        {
            snapshot = CaptureSnapshot(session);
        }
        catch (Exception ex)
        {
            Disconnect();
            logger.LogWarning(ex, "FluxorViewer: could not serialise the Snapshot for Session {SessionId}.", session.SessionId);
            return;
        }

        try
        {
            await connection.SendSnapshotAsync(snapshot, cancellationToken);
            await foreach (var pending in queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (TrySerialise(session, pending) is { } message)
                    await connection.SendActionsAsync([message], cancellationToken);
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

    /// <summary>
    /// Copies the tracked states and Sequence together and starts queueing, so the Snapshot matches the Actions
    /// that follow it exactly. Serialises outside the lock; the states are immutable.
    /// </summary>
    private Snapshot CaptureSnapshot(SessionInfo session)
    {
        long sequence;
        TrackedState[] tracked;
        lock (gate)
        {
            sequence = lastSequence;
            tracked = features.Select((f, i) => new TrackedState(f, states[i])).ToArray();
            connected = true;
        }

        return Messages.Snapshot(session, sequence, droppedCount: 0, tracked);
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
            connected = false;
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
