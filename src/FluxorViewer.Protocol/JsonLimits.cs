namespace FluxorViewer.Protocol;

/// <summary>Limits on messages and the JSON inside them (Payloads and Feature states), shared so both sides agree.</summary>
public static class JsonLimits
{
    /// <summary>The deepest nesting the middleware may send and the Viewer reads.</summary>
    public const int MaxDepth = 1024;

    /// <summary>
    /// The largest message, in bytes, the Viewer accepts, 32 MB: real Snapshots easily exceed SignalR's 32 KB default. The
    /// middleware splits and shrinks what it sends to stay under it.
    /// </summary>
    public const int MaxMessageSize = 32 * 1024 * 1024;
}
