namespace FluxorViewer.Server.Ingest;

public static class IngestLimits
{
    public const long MaximumMessageSize = 32 * 1024 * 1024;

    public const string CorsPolicy = "ingest";

    /// <summary>http or https on a loopback host (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>), any port.</summary>
    public static bool IsAllowedOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.IsLoopback;
}
