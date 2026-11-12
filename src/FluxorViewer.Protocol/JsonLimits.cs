namespace FluxorViewer.Protocol;

/// <summary>Limits on the JSON inside messages (Payloads and Feature states), shared so both sides agree.</summary>
public static class JsonLimits
{
    /// <summary>The deepest nesting the middleware may send and the Viewer reads.</summary>
    public const int MaxDepth = 1024;
}
