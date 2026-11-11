using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

/// <summary>A real Fluxor store reporting to a <see cref="FakeViewer"/>.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    public Harness(bool connectImmediately = false, int bufferSize = 1000, bool enabled = true, FakeViewer? viewer = null)
    {
        Viewer = viewer ?? new FakeViewer(connectImmediately);
        var services = new ServiceCollection();
        services.AddSingleton<IViewerConnectionFactory>(Viewer);
        services.AddSingleton<ILoggerFactory>(Logs);
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), typeof(CounterReducers), typeof(ProfileFeature), typeof(ProfileReducers),
                typeof(GoEffects), typeof(FlakyFeature))
            .UseFluxorViewer(v => { v.Enabled = enabled; v.BufferSize = bufferSize; }));
        provider = services.BuildServiceProvider();
    }

    public FakeViewer Viewer { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public IDispatcher Dispatcher => provider.GetRequiredService<IDispatcher>();

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    public Task InitializeAsync() => provider.GetRequiredService<IStore>().InitializeAsync();

    public async Task<FakeViewer.FakeConnection> Start()
    {
        await InitializeAsync();
        return await Viewer.FirstConnection.WaitAsync(Wait.Timeout, Wait.Ct);
    }

    /// <summary>
    /// Initialises the store, then lets the connection open and waits for the Snapshot, so later Actions are
    /// queued. The Snapshot covers StoreInitializedAction.
    /// </summary>
    public async Task<(FakeViewer.FakeConnection Connection, Snapshot Snapshot)> StartConnected()
    {
        var connection = await Start();
        Viewer.ConnectGate.TrySetResult();
        var snapshot = Assert.IsType<Snapshot>(await connection.FirstMessage.WaitAsync(Wait.Timeout, Wait.Ct));
        return (connection, snapshot);
    }

    public static async Task<IReadOnlyList<ActionRecorded>> ActionsSent(FakeViewer.FakeConnection connection, int count)
    {
        await Wait.Until(() => connection.Actions.Count >= count);
        return connection.Actions;
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
