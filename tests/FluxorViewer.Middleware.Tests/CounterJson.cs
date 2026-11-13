using System.Text.Json;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware.Tests;

/// <summary>Reads the Counter Feature's <c>Count</c> out of what the middleware sent.</summary>
internal static class CounterJson
{
    /// <summary>The <c>Count</c> in a Counter Feature state.</summary>
    public static int Count(FeatureState counter) =>
        JsonDocument.Parse(counter.StateJson).RootElement.GetProperty("Count").GetInt32();

    /// <summary>The Counter among <paramref name="features"/>, e.g. a Snapshot's or an Action's Changed Features.</summary>
    public static int Count(IEnumerable<FeatureState> features) => Count(features.Single(f => f.Name == "Counter"));

    /// <summary>The Counter's <c>Count</c> in a Snapshot.</summary>
    public static int Count(Snapshot snapshot) => Count(snapshot.Features);
}
