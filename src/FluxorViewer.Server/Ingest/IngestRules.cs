namespace FluxorViewer.Server.Ingest;

/// <summary>What the ingest hub accepts beyond the protocol itself: message size and which origins may connect.</summary>
public static class IngestRules
{
    /// <summary>The largest message the hub accepts, 32 MB: real Snapshots easily exceed SignalR's 32 KB default.</summary>
    public const long MaximumMessageSize = 32 * 1024 * 1024;

    /// <summary>The CORS policy that lets WebAssembly Host apps connect from the browser.</summary>
    public const string CorsPolicy = "ingest";

    /// <summary>http or https on a loopback host (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>), any port.</summary>
    public static bool IsAllowedOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.IsLoopback;
}
