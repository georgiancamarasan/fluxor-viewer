using Fluxor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxorViewer.Middleware;

/// <summary>Observes a Fluxor store and reports it to the Viewer as a Session.</summary>
/// <remarks>Scoped like the store, so the DI scope (e.g. a Blazor circuit) disposes it.</remarks>
internal sealed class FluxorViewerMiddleware(
    FluxorViewerOptions options,
    IViewerConnectionFactory connectionFactory,
    ILoggerFactory? loggerFactory = null)
    : Fluxor.Middleware, IAsyncDisposable, IDisposable
{
    internal FluxorViewerOptions Options { get; } = options;

    internal SessionReporter Reporter { get; } = new(
        options,
        connectionFactory,
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FluxorViewerMiddleware>());

    public override Task InitializeAsync(IDispatcher dispatcher, IStore store)
    {
        Reporter.Start(store);
        return Task.CompletedTask;
    }

    public override void AfterDispatch(object action) => Reporter.Record(action);

    public ValueTask DisposeAsync() => Reporter.DisposeAsync();

    // A synchronous scope dispose can't wait (and mustn't block in WebAssembly), so shut down in the background.
    public void Dispose() => _ = Reporter.DisposeAsync().AsTask();
}
