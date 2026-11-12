using System.Text.Json;
using FluxorViewer.Protocol;

namespace FluxorViewer.Server.Components;

/// <summary>How the Viewer reads the JSON a Host app sends.</summary>
public static class ReceivedJson
{
    /// <summary>Reads as deep as the middleware may send, well past JsonDocument's default of 64.</summary>
    public static readonly JsonDocumentOptions Options = new() { MaxDepth = JsonLimits.MaxDepth };

    public static JsonDocument Parse(string json) => JsonDocument.Parse(json, Options);
}
