using System.Net;
using System.Net.Sockets;

namespace FluxorViewer.Middleware.Tests;

/// <summary>
/// An HTTP endpoint on a free local port that answers every request with 404 and records the path of the first, so a
/// test can see where the real SignalR client looks for the Viewer without a Viewer to answer.
/// </summary>
internal sealed class RequestRecorder : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly TaskCompletionSource<string> firstPath = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts listening on a free port.</summary>
    public RequestRecorder()
    {
        BaseUrl = $"http://127.0.0.1:{FreePort()}";
        listener.Prefixes.Add(BaseUrl + "/");
        listener.Start();
        _ = AnswerAsync();
    }

    /// <summary>The endpoint's address, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>The path of the first request, once it arrives.</summary>
    public Task<string> FirstPath => firstPath.Task.WaitAsync(Wait.Timeout, Wait.CancellationToken);

    private async Task AnswerAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (!listener.IsListening)
            {
                return;
            }

            firstPath.TrySetResult(context.Request.Url!.AbsolutePath);
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => listener.Close();
}
