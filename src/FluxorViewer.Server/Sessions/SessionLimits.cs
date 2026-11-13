namespace FluxorViewer.Server.Sessions;

/// <summary>How much the Viewer keeps: entries per Session's History, and disconnected Sessions.</summary>
public sealed record SessionLimits(HistoryLimit History, DisconnectedSessionLimit Disconnected)
{
    /// <summary>Both limits at their defaults.</summary>
    public static readonly SessionLimits Default = new(HistoryLimit.Default, DisconnectedSessionLimit.Default);
}
