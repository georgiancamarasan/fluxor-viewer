using System.Runtime.CompilerServices;
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

    /// <param name="viewer">The Viewer to report to; by default one that isn't up yet (<see cref="FakeViewer.NotUpYet"/>).</param>
    /// <param name="types">Features, reducers and effects to scan besides the shared Counter, Profile and Flaky ones.</param>
    /// <param name="configure">Options beyond <c>Enabled</c>, such as a smaller <c>BufferSize</c>.</param>
    /// <remarks>Three parameters, each optional and independent: a test names only the one it needs.</remarks>
    public Harness(FakeViewer? viewer = null, Type[]? types = null, Action<FluxorViewerOptions>? configure = null)
    {
        Viewer = viewer ?? FakeViewer.NotUpYet();
        var services = new ServiceCollection();
        services.AddSingleton<IViewerConnectionFactory>(Viewer);
        services.AddSingleton<ILoggerFactory>(Logs);
        services.AddSingleton<TimeProvider>(Time);
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), [typeof(CounterReducers), typeof(ProfileFeature), typeof(ProfileReducers),
                typeof(GoEffects), typeof(FlakyFeature), .. types ?? []])
            .UseFluxorViewer(v =>
            {
                v.Enabled = true;
                configure?.Invoke(v);
            }));
        provider = services.BuildServiceProvider();
    }

    public FakeViewer Viewer { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    /// <summary>
    /// Moves time on in 100 ms steps, letting the sender run between them, until <paramref name="condition"/> holds;
    /// for waiting out a backoff.
    /// </summary>
    public Task AdvanceUntil(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "") =>
        Wait.Until(condition, () => Time.Advance(TimeSpan.FromMilliseconds(100)), description);

    public IDispatcher Dispatcher => provider.GetRequiredService<IDispatcher>();

    /// <summary>Dispatches <paramref name="count"/> increments, one after another.</summary>
    public void DispatchIncrements(int count)
    {
        for (var i = 0; i < count; i++)
            Dispatcher.Dispatch(new IncrementAction());
    }

    /// <summary>Advances time to the next connection attempt and says how many whole seconds that took.</summary>
    public async Task<double> SecondsToNextAttempt()
    {
        var from = Time.GetUtcNow();
        var attempts = Viewer.Connections.Count;
        await AdvanceUntil(() => Viewer.Connections.Count > attempts);
        return Math.Round((Time.GetUtcNow() - from).TotalSeconds);
    }

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    public Task InitializeAsync() => provider.GetRequiredService<IStore>().InitializeAsync();

    public async Task<FakeViewer.FakeConnection> Start()
    {
        await InitializeAsync();
        return await Viewer.FirstConnection.WaitAsync(Wait.Timeout, Wait.Ct);
    }

    /// <summary>
    /// Initialises the store, then lets the connection open and waits for the Snapshot (of the state before any Action) and
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
        await Wait.Until(() => ActionsAfterStoreInitialized(connection).Count >= count);
        return ActionsAfterStoreInitialized(connection);
    }

    private static List<ActionRecorded> ActionsAfterStoreInitialized(FakeViewer.FakeConnection connection) =>
        connection.Actions.Where(a => a.ActionType != "Fluxor.StoreInitializedAction").ToList();

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
