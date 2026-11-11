using FluxorViewer.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace FluxorViewer.Middleware;

/// <summary>Creates the connection a Session uses to report to the Viewer.</summary>
internal interface IViewerConnectionFactory
{
    IViewerConnection Create(Uri serverUrl);
}

/// <summary>One Session's connection to the Viewer's ingest hub.</summary>
internal interface IViewerConnection : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task SendSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken);
}

internal sealed class SignalRViewerConnectionFactory : IViewerConnectionFactory
{
    public IViewerConnection Create(Uri serverUrl) => new SignalRViewerConnection(
        new HubConnectionBuilder().WithUrl(HubUrl(serverUrl)).Build());

    /// <summary>The ingest hub under <paramref name="serverUrl"/>, keeping any path it has.</summary>
    internal static Uri HubUrl(Uri serverUrl)
    {
        var baseUrl = serverUrl.AbsoluteUri.EndsWith('/') ? serverUrl : new Uri(serverUrl.AbsoluteUri + "/");
        return new Uri(baseUrl, "hub/ingest");
    }

    private sealed class SignalRViewerConnection(HubConnection hub) : IViewerConnection
    {
        public Task StartAsync(CancellationToken cancellationToken) => hub.StartAsync(cancellationToken);

        public Task SendSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken) =>
            hub.InvokeAsync("Snapshot", snapshot, cancellationToken);

        public ValueTask DisposeAsync() => hub.DisposeAsync();
    }
}
