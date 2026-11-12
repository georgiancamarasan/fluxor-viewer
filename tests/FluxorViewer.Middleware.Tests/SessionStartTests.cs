using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-2: each store becomes a Session and sends a Snapshot once connected.
public class SessionStartTests
{
    private static TimeSpan Timeout => Wait.Timeout;

    private static CancellationToken Ct => Wait.Ct;

    private static ServiceProvider Provider(FakeViewer viewer, CapturingLoggerFactory? logs = null, params Type[] featureTypes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IViewerConnectionFactory>(viewer);
        if (logs is not null)
            services.AddSingleton<ILoggerFactory>(logs);
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), [typeof(CounterReducers), typeof(ProfileFeature), .. featureTypes])
            .UseFluxorViewer(v => { v.Enabled = true; v.AppName = "TestApp"; v.ServerUrl = "http://localhost:6000"; }));
        return services.BuildServiceProvider();
    }

    private static async Task<IStore> StartStore(IServiceProvider services)
    {
        var store = services.GetRequiredService<IStore>();
        await store.InitializeAsync();
        return store;
    }

    private static async Task<Snapshot> FirstSnapshot(FakeViewer viewer)
    {
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);
        return Assert.IsType<Snapshot>(await connection.FirstMessage.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task First_message_is_a_Snapshot_of_every_Feature()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer);
        await StartStore(provider);

        var snapshot = await FirstSnapshot(viewer);

        Assert.Equal(ProtocolVersion.Current, snapshot.ProtocolVersion);
        Assert.Equal("TestApp", snapshot.AppName);
        Assert.Equal(0, snapshot.LastSequence);
        Assert.Equal(0, snapshot.DroppedCount);
        Assert.Collection(snapshot.Features.OrderBy(f => f.Name),
            counter =>
            {
                Assert.Equal("Counter", counter.Name);
                Assert.Equal(typeof(CounterState).FullName, counter.StateType);
                Assert.Equal(0, JsonDocument.Parse(counter.StateJson).RootElement.GetProperty("Count").GetInt32());
            },
            profile =>
            {
                Assert.Equal("Profile", profile.Name);
                Assert.Equal(typeof(ProfileState).FullName, profile.StateType);
                Assert.Equal("Ada", JsonDocument.Parse(profile.StateJson).RootElement.GetProperty("Name").GetString());
            });
    }

    [Fact]
    public async Task Session_identity_is_set_on_initialize()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer);
        var before = DateTimeOffset.UtcNow;
        await StartStore(provider);

        var snapshot = await FirstSnapshot(viewer);

        Assert.NotEqual(Guid.Empty, snapshot.SessionId);
        Assert.Equal(Runtime.Server, snapshot.Runtime); // WebAssembly is checked by hand in MW-8.
        Assert.InRange(snapshot.StartedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, snapshot.StartedAt.Offset);
        Assert.Equal(new Uri("http://localhost:6000"), Assert.Single(viewer.Connections).ServerUrl);
    }

    [Fact]
    public async Task Two_stores_are_two_Sessions()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer);
        await using var circuit1 = provider.CreateAsyncScope();
        await using var circuit2 = provider.CreateAsyncScope();
        await StartStore(circuit1.ServiceProvider);
        await StartStore(circuit2.ServiceProvider);

        var snapshots = await Task.WhenAll(viewer.Connections.Select(c => c.FirstMessage.WaitAsync(Timeout, Ct)));

        Assert.Equal(2, snapshots.OfType<Snapshot>().Select(s => s.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Initialize_does_not_wait_for_the_connection()
    {
        var viewer = new FakeViewer(connectImmediately: false);
        await using var provider = Provider(viewer);

        await StartStore(provider).WaitAsync(Timeout, Ct);

        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);
        Assert.Empty(connection.Sent);
    }

    [Fact]
    public async Task The_first_Snapshot_is_the_initial_state_even_if_Actions_came_before_the_connect()
    {
        var viewer = new FakeViewer(connectImmediately: false);
        await using var provider = Provider(viewer);
        await StartStore(provider);
        provider.GetRequiredService<IDispatcher>().Dispatch(new IncrementAction());

        viewer.ConnectGate.SetResult();
        var snapshot = await FirstSnapshot(viewer);

        var counter = Assert.Single(snapshot.Features, f => f.Name == "Counter");
        Assert.Equal(0, JsonDocument.Parse(counter.StateJson).RootElement.GetProperty("Count").GetInt32());
        Assert.Equal(0, snapshot.LastSequence);
    }

    [Fact]
    public async Task A_failed_connect_is_logged_at_debug_and_the_store_keeps_working()
    {
        var logs = new CapturingLoggerFactory();
        var viewer = new FakeViewer { ConnectFailure = new HttpRequestException("Connection refused") };
        await using var provider = Provider(viewer, logs);
        await StartStore(provider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);

        await Wait.Until(() => logs.Entries.Any(e => e.Exception is HttpRequestException));

        Assert.Empty(connection.Sent);
        Assert.Equal(LogLevel.Debug, logs.Entries.Single(e => e.Exception is HttpRequestException).Level);
        provider.GetRequiredService<IDispatcher>().Dispatch(new IncrementAction());
        Assert.Equal(1, provider.GetRequiredService<IState<CounterState>>().Value.Count);
    }

    [Fact]
    public async Task A_Viewer_rejection_is_logged_at_warning_with_its_reason()
    {
        var logs = new CapturingLoggerFactory();
        var viewer = new FakeViewer { SnapshotFailure = new Microsoft.AspNetCore.SignalR.HubException("FluxorViewer: protocol version 2.0 is not compatible") };
        await using var provider = Provider(viewer, logs);
        await StartStore(provider);

        await Wait.Until(() => logs.Entries.Any(e => e.Level == LogLevel.Warning));

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("protocol version 2.0 is not compatible", warning.Message);
    }

    [Fact]
    public async Task A_network_failure_sending_the_Snapshot_stays_at_debug()
    {
        var logs = new CapturingLoggerFactory();
        var viewer = new FakeViewer { SnapshotFailure = new HttpRequestException("Reset") };
        await using var provider = Provider(viewer, logs);
        await StartStore(provider);

        await Wait.Until(() => logs.Entries.Any(e => e.Exception is HttpRequestException));

        Assert.Equal(LogLevel.Debug, logs.Entries.Single(e => e.Exception is HttpRequestException).Level);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Disposing_the_store_cancels_a_pending_connect_and_closes_the_connection()
    {
        var viewer = new FakeViewer(connectImmediately: false);
        var provider = Provider(viewer);
        var circuit = provider.CreateAsyncScope();
        await StartStore(circuit.ServiceProvider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);

        await circuit.DisposeAsync().AsTask().WaitAsync(Timeout, Ct);

        Assert.True(connection.StartToken.IsCancellationRequested);
        Assert.True(connection.Disposed.IsCompleted);
        Assert.Empty(connection.Sent);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_store_closes_an_open_connection()
    {
        var viewer = new FakeViewer();
        var provider = Provider(viewer);
        var circuit = provider.CreateAsyncScope();
        await StartStore(circuit.ServiceProvider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);
        await connection.FirstMessage.WaitAsync(Timeout, Ct);

        await circuit.DisposeAsync();

        Assert.True(connection.Disposed.IsCompleted);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_synchronous_scope_dispose_still_closes_the_connection()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer);
        var circuit = provider.CreateScope();
        await StartStore(circuit.ServiceProvider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Ct);
        await connection.FirstMessage.WaitAsync(Timeout, Ct);

        circuit.Dispose();

        await connection.Disposed.WaitAsync(Timeout, Ct);
    }

    [Theory]
    [InlineData("http://localhost:5299", "http://localhost:5299/hub/ingest")]
    [InlineData("http://localhost:5299/", "http://localhost:5299/hub/ingest")]
    [InlineData("http://127.0.0.1:5299/viewer", "http://127.0.0.1:5299/viewer/hub/ingest")]
    [InlineData("https://host.docker.internal:5299/viewer/", "https://host.docker.internal:5299/viewer/hub/ingest")]
    public void Hub_url_is_under_the_server_url(string serverUrl, string expected) =>
        Assert.Equal(new Uri(expected), SignalRViewerConnectionFactory.HubUrl(new Uri(serverUrl)));

    [Fact]
    public void Registration_uses_SignalR_by_default()
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => o.ScanTypes(typeof(CounterFeature)).UseFluxorViewer(v => v.Enabled = true));

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IViewerConnectionFactory) && d.ImplementationType == typeof(SignalRViewerConnectionFactory));
    }
}

public record ProfileState(string Name);

public class ProfileFeature : Feature<ProfileState>
{
    public override string GetName() => "Profile";
    protected override ProfileState GetInitialState() => new("Ada");
}
