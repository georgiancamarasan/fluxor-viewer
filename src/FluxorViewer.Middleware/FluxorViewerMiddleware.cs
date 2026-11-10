namespace FluxorViewer.Middleware;

/// <summary>Observes a Fluxor store and reports it to the Viewer as a Session.</summary>
internal sealed class FluxorViewerMiddleware(FluxorViewerOptions options) : Fluxor.Middleware
{
    internal FluxorViewerOptions Options { get; } = options;
}
