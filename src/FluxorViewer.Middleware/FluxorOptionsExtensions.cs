using System.Runtime.CompilerServices;
using Fluxor.DependencyInjection;
using FluxorViewer.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// In Fluxor's namespace so it shows up in AddFluxor(o => o.…) without an extra using.
namespace Fluxor;

/// <summary>Adds the Fluxor Viewer to a Fluxor store's options: <c>AddFluxor(o => o.UseFluxorViewer(…))</c>.</summary>
public static class FluxorViewerFluxorOptionsExtensions
{
    // Tracks calls per FluxorOptions; a disabled call registers nothing, so the service collection can't tell us.
    private static readonly ConditionalWeakTable<FluxorOptions, object> Configured = new();

    /// <summary>
    /// Reports this store to the Fluxor Viewer. Does nothing unless <see cref="FluxorViewerOptions.Enabled"/> is set.
    /// </summary>
    /// <exception cref="ArgumentException">Enabled with invalid options.</exception>
    /// <exception cref="InvalidOperationException">Called more than once on the same Fluxor options.</exception>
    public static FluxorOptions UseFluxorViewer(this FluxorOptions options, Action<FluxorViewerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Configured.TryAdd(options, new object()))
            throw new InvalidOperationException("FluxorViewer: UseFluxorViewer has already been called for this store.");

        var viewerOptions = new FluxorViewerOptions();
        configure?.Invoke(viewerOptions);

        if (!viewerOptions.Enabled)
            return options;

        viewerOptions.Validate();
        options.Services.AddSingleton(viewerOptions);
        options.Services.TryAddSingleton<IViewerConnectionFactory, SignalRViewerConnectionFactory>();
        options.Services.TryAddSingleton<ConnectionWarning>();
        return options.AddMiddleware<FluxorViewerMiddleware>();
    }
}
