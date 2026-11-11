using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Reports one store to the Viewer as a Session: connects in the background and sends a Snapshot
/// once connected. Never throws into the Host app.
/// </summary>
internal sealed class SessionReporter(FluxorViewerOptions options, IViewerConnectionFactory connectionFactory, ILogger logger)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
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

        run = RunAsync(store, Session, stopping.Token);
    }

    private async Task RunAsync(IStore store, SessionInfo session, CancellationToken cancellationToken)
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
            // Captured now, not at Start, so it reflects anything dispatched while connecting.
            snapshot = SnapshotCapture.Capture(store, session, lastSequence: 0, droppedCount: 0);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FluxorViewer: could not serialise the Snapshot for Session {SessionId}.", session.SessionId);
            return;
        }

        try
        {
            await connection.SendSnapshotAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FluxorViewer: could not send the Snapshot for Session {SessionId}.", session.SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

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
