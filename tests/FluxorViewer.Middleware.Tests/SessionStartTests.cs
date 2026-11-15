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

    [Fact]
    public async Task First_message_is_a_Snapshot_of_every_Feature()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer);
        await StartStore(provider);

        var snapshot = await viewer.FirstSnapshot();

        Assert.Equal(ProtocolVersion.Current, snapshot.ProtocolVersion);
        Assert.Equal("TestApp", snapshot.AppName);
        Assert.Equal(0, snapshot.LastSequence);
        Assert.Equal(0, snapshot.DroppedCount);
        Assert.Collection(snapshot.Features.OrderBy(f => f.Name),
            counter =>
            {
                Assert.Equal("Counter", counter.Name);
                Assert.Equal("FluxorViewer.Middleware.Tests.CounterState", counter.StateType);
                Assert.Equal(0, CounterJson.Count(counter));
            },
            profile =>
            {
                Assert.Equal("Profile", profile.Name);
                Assert.Equal("FluxorViewer.Middleware.Tests.ProfileState", profile.StateType);
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

        var snapshot = await viewer.FirstSnapshot();

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

        var snapshots = await Task.WhenAll(viewer.Connections.Select(c => c.FirstMessage.WaitAsync(Timeout, Wait.CancellationToken)));

        Assert.Equal(2, snapshots.OfType<Snapshot>().Select(s => s.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Initialize_does_not_wait_for_the_connection()
    {
        var viewer = FakeViewer.NotUpYet();
        await using var provider = Provider(viewer);

        await StartStore(provider).WaitAsync(Timeout, Wait.CancellationToken);

        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Wait.CancellationToken);
        Assert.Empty(connection.Sent);
    }

    [Fact]
    public async Task The_first_Snapshot_is_of_the_state_before_any_Action_even_if_Actions_came_before_the_connect()
    {
        var viewer = FakeViewer.NotUpYet();
        await using var provider = Provider(viewer);
        await StartStore(provider);
        provider.GetRequiredService<IDispatcher>().Dispatch(new IncrementAction());

        viewer.ConnectGate.SetResult();
        var snapshot = await viewer.FirstSnapshot();

        var counter = Assert.Single(snapshot.Features, f => f.Name == "Counter");
        Assert.Equal(0, CounterJson.Count(counter));
        Assert.Equal(0, snapshot.LastSequence);
    }

    [Fact]
    public async Task A_failed_connect_is_logged_and_the_store_keeps_working()
    {
        var logs = new CapturingLoggerFactory();
        var viewer = new FakeViewer { ConnectFailure = new HttpRequestException("Connection refused") };
        await using var provider = Provider(viewer, logs);

        await StartStore(provider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Wait.CancellationToken);
        // Its level is ConnectionWarningTests' concern: Warning only for the first failure to this ServerUrl in the process.
        await Wait.Until(() => logs.Entries.Any(e => e.Message.Contains("http://localhost:6000")));
        Assert.Empty(connection.Sent);

        provider.GetRequiredService<IDispatcher>().Dispatch(new IncrementAction());
        Assert.Equal(1, provider.GetRequiredService<IState<CounterState>>().Value.Count);
    }

    [Fact]
    public async Task A_Viewer_rejection_is_logged_at_warning_with_its_reason()
    {
        var logs = new CapturingLoggerFactory();
        var viewer = new FakeViewer { SnapshotFailure = new Microsoft.AspNetCore.SignalR.HubException(
            "An unexpected error occurred invoking 'Snapshot' on the server. HubException: FluxorViewer: protocol version 2.0 is not compatible") };
        await using var provider = Provider(viewer, logs);
        await StartStore(provider);

        await Wait.Until(() => logs.Entries.Any(e => e.Level == LogLevel.Warning));

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("protocol version 2.0 is not compatible", warning.Message);
        Assert.DoesNotContain("unexpected error", warning.Message); // Just the Viewer's reason.
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
        var viewer = FakeViewer.NotUpYet();
        var provider = Provider(viewer);
        var circuit = provider.CreateAsyncScope();
        await StartStore(circuit.ServiceProvider);
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Wait.CancellationToken);

        await circuit.DisposeAsync().AsTask().WaitAsync(Timeout, Wait.CancellationToken);

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
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Wait.CancellationToken);
        await connection.FirstMessage.WaitAsync(Timeout, Wait.CancellationToken);

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
        var connection = await viewer.FirstConnection.WaitAsync(Timeout, Wait.CancellationToken);
        await connection.FirstMessage.WaitAsync(Timeout, Wait.CancellationToken);

        circuit.Dispose();

        await connection.Disposed.WaitAsync(Timeout, Wait.CancellationToken);
    }

    [Theory]
    [InlineData("", "/hub/ingest/negotiate")]
    [InlineData("/", "/hub/ingest/negotiate")]
    [InlineData("/viewer", "/viewer/hub/ingest/negotiate")]
    [InlineData("/viewer/", "/viewer/hub/ingest/negotiate")]
    public async Task By_default_it_looks_for_the_ingest_hub_under_the_ServerUrl_keeping_its_path(string path, string requested)
    {
        using var endpoint = new RequestRecorder();
        var services = new ServiceCollection();
        services.AddFluxor(o => o
            .ScanTypes(typeof(CounterFeature), typeof(CounterReducers))
            .UseFluxorViewer(v => { v.Enabled = true; v.ServerUrl = endpoint.BaseUrl + path; }));
        await using var provider = services.BuildServiceProvider();

        await StartStore(provider);

        Assert.Equal(requested, await endpoint.FirstPath); // SignalR's negotiate request, under the hub's URL
    }
}

public record ProfileState(string Name);

public class ProfileFeature : Feature<ProfileState>
{
    public override string GetName() => "Profile";
    protected override ProfileState GetInitialState() => new("Ada");
}
