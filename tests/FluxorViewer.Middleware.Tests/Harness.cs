using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace FluxorViewer.Middleware.Tests;

/// <summary>A real Fluxor store reporting to a <see cref="FakeViewer"/>, with time that only moves when told to.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    public Harness(bool connectImmediately = false, int bufferSize = 1000, bool enabled = true, FakeViewer? viewer = null,
        Type[]? types = null, Action<FluxorViewerOptions>? configure = null)
    {
        Viewer = viewer ?? new FakeViewer(connectImmediately);
        var services = new ServiceCollection();
        services.AddSingleton<IViewerConnectionFactory>(Viewer);
        services.AddSingleton<ILoggerFactory>(Logs);
        services.AddSingleton<TimeProvider>(Time);
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), [typeof(CounterReducers), typeof(ProfileFeature), typeof(ProfileReducers),
                typeof(GoEffects), typeof(FlakyFeature), .. types ?? []])
            .UseFluxorViewer(v =>
            {
                v.Enabled = enabled;
                v.BufferSize = bufferSize;
                configure?.Invoke(v);
            }));
        provider = services.BuildServiceProvider();
    }

    public FakeViewer Viewer { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    /// <summary>
    /// Moves time on in small steps, letting the sender run between them, until <paramref name="condition"/> holds;
    /// for waiting out a backoff.
    /// </summary>
    public async Task AdvanceUntil(Func<bool> condition, TimeSpan? step = null)
    {
        var deadline = DateTime.UtcNow + Wait.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            Time.Advance(step ?? TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, Wait.Ct);
        }
    }

    public IDispatcher Dispatcher => provider.GetRequiredService<IDispatcher>();

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    public Task InitializeAsync() => provider.GetRequiredService<IStore>().InitializeAsync();

    public async Task<FakeViewer.FakeConnection> Start()
    {
        await InitializeAsync();
        return await Viewer.FirstConnection.WaitAsync(Wait.Timeout, Wait.Ct);
    }

    /// <summary>
    /// Initialises the store, then lets the connection open and waits for the Snapshot (of the initial state) and
    /// StoreInitializedAction after it, so the sender is idle when the test starts dispatching.
    /// </summary>
    public async Task<(FakeViewer.FakeConnection Connection, Snapshot Snapshot)> StartConnected()
    {
        var connection = await Start();
        Viewer.ConnectGate.TrySetResult();
        var snapshot = Assert.IsType<Snapshot>(await connection.FirstMessage.WaitAsync(Wait.Timeout, Wait.Ct));
        await Wait.Until(() => connection.Actions.Any(a => a.Sequence == 1));
        return (connection, snapshot);
    }

    /// <summary>Waits for <paramref name="count"/> Actions other than StoreInitializedAction and returns them.</summary>
    public static async Task<IReadOnlyList<ActionRecorded>> ActionsSent(FakeViewer.FakeConnection connection, int count)
    {
        await Wait.Until(() => Dispatched(connection).Count >= count);
        return Dispatched(connection);
    }

    private static List<ActionRecorded> Dispatched(FakeViewer.FakeConnection connection) =>
        connection.Actions.Where(a => a.ActionType != "Fluxor.StoreInitializedAction").ToList();

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
