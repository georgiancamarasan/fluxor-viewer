using System.Globalization;

namespace FluxorViewer.Server.Components;

/// <summary>How the notes about the Viewer's limits say how much it keeps.</summary>
internal static class LimitPhrase
{
    /// <summary>"the Viewer keeps the last 1,000": <paramref name="count"/> grouped in thousands, the same in any culture.</summary>
    public static string KeepsTheLast(int count) => $"the Viewer keeps the last {count.ToString("#,0", CultureInfo.InvariantCulture)}";
}
