using System.Text.Json.Serialization;

namespace FluxorViewer.Protocol;

/// <summary>Where the Session's Fluxor store runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Runtime>))]
public enum Runtime
{
    /// <summary>On the server: Blazor Server, or the server half of an InteractiveAuto app.</summary>
    Server,

    /// <summary>In the browser: Blazor WebAssembly, or the WebAssembly half of an InteractiveAuto app.</summary>
    WebAssembly,
}
