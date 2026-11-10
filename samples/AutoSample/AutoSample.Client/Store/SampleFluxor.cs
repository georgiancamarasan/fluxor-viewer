using Fluxor;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSample.Client.Store;

public static class SampleFluxor
{
    /// <summary>
    /// Registers Fluxor with the sample's Features. Called from both the Server and the
    /// WebAssembly host so the two stores are identical.
    /// </summary>
    public static IServiceCollection AddSampleFluxor(this IServiceCollection services) =>
        services.AddFluxor(o => o.ScanAssemblies(typeof(SampleFluxor).Assembly));
}
