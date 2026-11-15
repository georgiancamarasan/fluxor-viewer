using System.Collections.Concurrent;
using System.Diagnostics;
using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Sends a Session's buffered Actions to the Viewer, each connection starting with a Snapshot of the base. On any failure
/// it waits out the backoff and starts over with a new connection, until the Session ends or the Viewer rejects it. Logs
/// rejections, Actions it can't build and the first connection to a ServerUrl that won't open at Warning, and other
/// failed or lost connections at Debug.
/// </summary>
internal sealed class Sender(ReporterContext context, SessionInfo session, ActionBuffer buffer)
{
    /// <summary>Delays between connection attempts; the last repeats.</summary>
    internal static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

    // ServerUrls whose connection has failed to open in this process: only the first failure is a Warning (PK-5).
    private static readonly ConcurrentDictionary<string, byte> WarnedServerUrls = new();

    private readonly ViewerJson json = new(context.Options, context.Logger);

    // Connection attempts since the Viewer last accepted a Snapshot; picks the backoff.
    private int failedAttempts;

    /// <summary>Runs until <paramref name="cancellationToken"/> ends the Session, or the Viewer rejects it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await RunConnectionAsync(cancellationToken) && await WaitOutBackoffAsync(cancellationToken))
        {
        }
    }

    /// <summary>One connection, from opening it until it fails; whether to try again with a new one.</summary>
    private async Task<bool> RunConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = context.Connections.Create(new Uri(context.Options.ServerUrl));
        var opened = false;
        try
        {
            await connection.StartAsync(cancellationToken);
            opened = true;
            buffer.Reconnected();
            return await SendAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HubException ex) when (Rejection.IsRejection(ex.Message))
        {
            // The Viewer refused us (e.g. protocol version mismatch): retrying won't help, and the developer needs to see
            // why. Any other HubException is an unexpected error in the Viewer, retried like a lost connection.
            StopRejected(Rejection.Reason(ex.Message));
            return false;
        }
        catch (Exception ex) when (!opened)
        {
            LogCouldNotConnect(ex);
            return true;
        }
        catch (Exception ex)
        {
            LogLost(ex);
            return true;
        }
        finally
        {
            await DisposeQuietlyAsync(connection);
        }
    }

    /// <summary>
    /// Sends a Snapshot when due and Actions as they're buffered; true when the Viewer closed the connection (try again),
    /// false when a Snapshot couldn't be built (the Session stops). Throws when sending fails.
    /// </summary>
    private async Task<bool> SendAsync(IViewerConnection connection, CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (buffer.TakeNext())
            {
                case BaseCopy baseCopy:
                    if (!await TrySendBaseAsync(connection, baseCopy, cancellationToken))
                        return false;
                    break;
                case Batch { Actions.Count: 0 }:
                    if (!await buffer.WaitForWorkAsync(connection.Closed, cancellationToken))
                    {
                        LogLost(null);
                        return true;
                    }
                    break;
                case Batch batch:
                    if (!await SendBatchAsync(connection, batch, cancellationToken))
                        return false;
                    break;
            }
        }
    }

    /// <summary>
    /// Builds and sends a Snapshot of the buffer's base, then tells the buffer the drops it reported are settled; false if
    /// it couldn't be built, which stops the Session and logs a Warning.
    /// </summary>
    private async Task<bool> TrySendBaseAsync(IViewerConnection connection, BaseCopy baseCopy, CancellationToken cancellationToken)
    {
        if (!await TrySendSnapshotAsync(connection, baseCopy, cancellationToken))
            return false;
        buffer.SnapshotSent(baseCopy);
        return true;
    }

    /// <summary>
    /// Builds and sends a Snapshot of <paramref name="baseCopy"/>; false if it couldn't be built, which stops the Session
    /// and logs a Warning.
    /// </summary>
    private async Task<bool> TrySendSnapshotAsync(IViewerConnection connection, BaseCopy baseCopy, CancellationToken cancellationToken)
    {
        Snapshot snapshot;
        try
        {
            snapshot = Messages.Snapshot(session, baseCopy, json);
        }
        catch (Exception ex)
        {
            buffer.Stop();
            context.Logger.LogWarning(ex, "FluxorViewer: could not build the Snapshot for Session {SessionId}.", session.SessionId);
            return false;
        }

        await connection.SendSnapshotAsync(snapshot, cancellationToken);
        failedAttempts = 0; // Accepted: the next failure starts the backoff over.
        return true;
    }

    /// <summary>
    /// Sends <paramref name="batch"/> as <see cref="InSendingOrder"/> lays it out; false if a Snapshot after Actions that
    /// can't be built couldn't be built itself, which stops the Session and logs a Warning.
    /// </summary>
    private async Task<bool> SendBatchAsync(IViewerConnection connection, Batch batch, CancellationToken cancellationToken)
    {
        foreach (var message in InSendingOrder(batch))
        {
            var sent = message switch
            {
                PartMessage(var actions) => await SendPartAsync(connection, actions, cancellationToken),
                SnapshotMessage(var snapshotBase) => await TrySendSnapshotAsync(connection, snapshotBase, cancellationToken),
                _ => throw new UnreachableException(),
            };
            if (!sent)
                return false;
        }

        return true;
    }

    /// <summary>
    /// What <paramref name="batch"/> sends, in order: its Actions' messages in parts that fit the Viewer's limit, and after
    /// each run of Actions that can't be built, which are Dropped Actions, a Snapshot of the state just after the run
    /// counting them, so the Viewer records a Resync there instead of building on the wrong state (ADR 0005). Builds
    /// each Action as it goes, holding at most one part.
    /// </summary>
    private IEnumerable<BatchMessage> InSendingOrder(Batch batch)
    {
        var parts = new ActionParts(json);
        var droppedCount = 0;
        for (var index = 0; index < batch.Actions.Count; index++)
        {
            if (TryBuild(batch.Actions[index]) is not { } action)
            {
                droppedCount++;
                continue;
            }

            if (droppedCount > 0)
            {
                yield return new PartMessage(parts.Flush());
                yield return new SnapshotMessage(batch.SnapshotAfter(index - 1, droppedCount));
                droppedCount = 0;
            }

            yield return new PartMessage(parts.Add(action));
        }

        yield return new PartMessage(parts.Flush());
        if (droppedCount > 0)
            yield return new SnapshotMessage(batch.SnapshotAfter(batch.Actions.Count - 1, droppedCount));
    }

    /// <summary>Sends <paramref name="actions"/> as one call, unless there are none; always true.</summary>
    private static async Task<bool> SendPartAsync(IViewerConnection connection, IReadOnlyList<ActionRecorded> actions, CancellationToken cancellationToken)
    {
        if (actions.Count > 0)
            await connection.SendActionsAsync(actions, cancellationToken);
        return true;
    }

    /// <summary>One thing <see cref="InSendingOrder"/> lays out for a batch to send.</summary>
    private abstract record BatchMessage;

    /// <summary>Actions to send in one call; possibly none.</summary>
    private sealed record PartMessage(IReadOnlyList<ActionRecorded> Actions) : BatchMessage;

    /// <summary>A Snapshot to send after Actions that couldn't be built.</summary>
    private sealed record SnapshotMessage(BaseCopy Base) : BatchMessage;

    /// <summary>The message for <paramref name="action"/>, or null (logged at Warning) if it can't be built.</summary>
    private ActionRecorded? TryBuild(PendingAction action)
    {
        try
        {
            return Messages.ActionRecorded(session, action, json);
        }
        catch (Exception ex)
        {
            // A value that won't serialise or fit is already a Serialisation error; this is anything else, e.g. a Feature's
            // name, or a message still over the Viewer's limit with every value left out.
            context.Logger.LogWarning(ex, "FluxorViewer: could not build Action {Sequence} ({ActionType}); dropped.",
                action.Sequence, action.Action.GetType());
            return null;
        }
    }

    /// <summary>Waits before the next connection attempt, longer after each failure; false if the Session ended meanwhile.</summary>
    private async Task<bool> WaitOutBackoffAsync(CancellationToken cancellationToken)
    {
        var delay = Backoff[Math.Min(failedAttempts, Backoff.Length - 1)];
        failedAttempts++;
        try
        {
            await Task.Delay(delay, context.Time, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Stops the Session for good and tells the developer why, at Warning.</summary>
    private void StopRejected(string reason)
    {
        buffer.Stop();
        context.Logger.LogWarning("FluxorViewer: the Viewer rejected Session {SessionId}: {Reason}", session.SessionId, reason);
    }

    /// <summary>
    /// Logs a connection that wouldn't open: at Warning the first time for this ServerUrl in the process, so a developer
    /// whose Session never shows up sees why without Debug logging; at Debug after that, like a lost connection.
    /// </summary>
    private void LogCouldNotConnect(Exception exception)
    {
        if (!WarnedServerUrls.TryAdd(context.Options.ServerUrl, 0))
        {
            LogLost(exception);
            return;
        }

        context.Logger.LogWarning(
            "FluxorViewer: couldn't connect to the Viewer at {ServerUrl} ({Reason}); retrying, and Actions are kept until it connects. " +
            "Is fluxor-viewer running, and does ServerUrl match the URL it printed?",
            context.Options.ServerUrl, exception.Message);
    }

    private void LogLost(Exception? exception) =>
        context.Logger.LogDebug(exception, "FluxorViewer: lost or couldn't open the connection to the Viewer at {ServerUrl} for Session {SessionId}.",
            context.Options.ServerUrl, session.SessionId);

    private async Task DisposeQuietlyAsync(IViewerConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "FluxorViewer: error closing the connection to the Viewer.");
        }
    }
}
