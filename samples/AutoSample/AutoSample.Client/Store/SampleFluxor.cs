using Fluxor;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSample.Client.Store;

public static class SampleFluxor
{
    /// <summary>
    /// Registers Fluxor with the sample's Features and reports to the Fluxor Viewer at its default address. Called
    /// from both the Server and the WebAssembly host so the two stores are identical. AppName is set because it would
    /// otherwise default to each host's entry assembly (AutoSample and AutoSample.Client), showing as two Host apps.
    /// </summary>
    /// <param name="viewerEnabled">Whether to report to the Viewer; each host passes its own IsDevelopment().</param>
    public static IServiceCollection AddSampleFluxor(this IServiceCollection services, bool viewerEnabled) =>
        services.AddFluxor(o => o
            .ScanAssemblies(typeof(SampleFluxor).Assembly)
            .UseFluxorViewer(v =>
            {
                v.Enabled = viewerEnabled;
                v.AppName = "AutoSample";
            }));
}
