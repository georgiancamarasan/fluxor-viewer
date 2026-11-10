using System.Text.Json.Serialization;

namespace FluxorViewer.Protocol;

/// <summary>Where the Session's Fluxor store runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Runtime>))]
public enum Runtime
{
    Server,
    WebAssembly,
}
