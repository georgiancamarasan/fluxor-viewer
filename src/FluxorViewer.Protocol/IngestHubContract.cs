namespace FluxorViewer.Protocol;

/// <summary>Where the Viewer receives Sessions, and the hub methods the middleware calls there.</summary>
public static class IngestHubContract
{
    /// <summary>The hub's path on the Viewer, relative to its base URL.</summary>
    public const string Path = "hub/ingest";

    /// <summary>Takes a <see cref="Protocol.Snapshot"/>; the first on a connection binds it to that Session.</summary>
    public const string Snapshot = "Snapshot";

    /// <summary>Takes an array of <see cref="ActionRecorded"/> for the Session bound to the connection.</summary>
    public const string Actions = "Actions";
}
