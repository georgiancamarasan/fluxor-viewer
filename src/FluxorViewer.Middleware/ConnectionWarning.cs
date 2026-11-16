namespace FluxorViewer.Middleware;

/// <summary>
/// Whether the Host app has already warned that a connection to the Viewer wouldn't open: only the first such failure
/// in the process is a Warning (PK-5).
/// </summary>
/// <remarks>
/// A singleton, so every Session in the app shares it, and an app has one container per process. Not static, so each
/// container, and each test, starts unwarned.
/// </remarks>
internal sealed class ConnectionWarning
{
    private int warned;

    /// <summary>True the first time only, when the caller logs the Warning; false after that.</summary>
    public bool TryClaim() => Interlocked.Exchange(ref warned, 1) == 0;
}
