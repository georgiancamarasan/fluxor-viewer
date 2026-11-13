using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Sends a Session's buffered Actions to the Viewer, each connection starting with a Snapshot of the base. On any failure
/// it waits out the backoff and starts over with a new connection, until the Session ends or the Viewer rejects it. Logs
/// rejections and Actions it can't build at Warning, and lost connections at Debug.
/// </summary>
internal sealed class Sender(ReporterContext context, SessionInfo session, ActionBuffer buffer)
{
    /// <summary>Delays between connection attempts; the last repeats.</summary>
    internal static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

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
        try
        {
            await connection.StartAsync(cancellationToken);
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
                    if (!await TrySendSnapshotAsync(connection, baseCopy, cancellationToken))
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
                    await SendBatchAsync(connection, batch, cancellationToken);
                    break;
            }
        }
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
        buffer.SnapshotSent(baseCopy);
        return true;
    }

    private async Task SendBatchAsync(IViewerConnection connection, Batch batch, CancellationToken cancellationToken)
    {
        var messages = batch.Actions.Select(TryBuild).OfType<ActionRecorded>();
        foreach (var part in WireJson.Split(messages, json))
            await connection.SendActionsAsync(part, cancellationToken);
    }

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
            context.Logger.LogWarning(ex, "FluxorViewer: could not build Action {Sequence} ({ActionType}); skipped.",
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
