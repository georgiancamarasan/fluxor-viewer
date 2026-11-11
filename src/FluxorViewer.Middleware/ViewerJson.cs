using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxorViewer.Middleware;

/// <summary>Serialises Feature states and Actions for the Viewer.</summary>
internal static class ViewerJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 64,
    };

    public static string Serialize(object? value, Type type) => JsonSerializer.Serialize(value, type, Options);
}
