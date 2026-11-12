using Microsoft.JSInterop;

namespace FluxorViewer.Server.Components;

/// <summary>A component's collocated JS module (<c>X.razor.js</c>), imported on first use and released with the component.</summary>
public sealed class CollocatedModule(IJSRuntime js, string path) : IAsyncDisposable
{
    private IJSObjectReference? module;

    /// <summary>Calls the module's exported <paramref name="function"/> and returns its result.</summary>
    public async ValueTask<T> InvokeAsync<T>(string function, params object?[] args) =>
        await (await ImportAsync()).InvokeAsync<T>(function, args);

    /// <summary>Calls the module's exported <paramref name="function"/>.</summary>
    public async ValueTask InvokeVoidAsync(string function, params object?[] args) =>
        await (await ImportAsync()).InvokeVoidAsync(function, args);

    /// <summary>Releases the module, if it was imported; quietly when the circuit is already gone.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module is not null)
                await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the module with it.
        }
    }

    private async ValueTask<IJSObjectReference> ImportAsync() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", path);
}
