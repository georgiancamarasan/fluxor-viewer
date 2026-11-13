using Fluxor;
using FluxorViewer.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Middleware.Tests;

// MW-1: UseFluxorViewer registers the middleware with its options, and only when enabled.
public class UseFluxorViewerTests
{
    private static readonly System.Reflection.Assembly MiddlewareAssembly = typeof(FluxorViewerOptions).Assembly;

    private static ServiceCollection Services(Action<Fluxor.DependencyInjection.FluxorOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => configure(o.ScanTypes(typeof(CounterFeature), typeof(CounterReducers))));
        return services;
    }

    private static ServiceProvider Provider(FakeViewer viewer, Action<FluxorViewerOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IViewerConnectionFactory>(viewer);
        services.AddFluxor(o => o.ScanTypes(typeof(CounterFeature), typeof(CounterReducers)).UseFluxorViewer(configure));
        return services.BuildServiceProvider();
    }

    private static void DispatchIncrements(IServiceProvider provider, int count)
    {
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        for (var i = 0; i < count; i++)
            dispatcher.Dispatch(new IncrementAction());
    }

    private static async Task<IReadOnlyList<IMiddleware>> MiddlewaresOf(ServiceCollection services)
    {
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IStore>();
        await store.InitializeAsync();
        return store.GetMiddlewares().ToList();
    }

    [Fact]
    public async Task Disabled_registers_nothing()
    {
        var services = Services(o => o.UseFluxorViewer(v => v.ServerUrl = "http://localhost:1234"));

        Assert.DoesNotContain(services, d =>
            d.ServiceType.Assembly == MiddlewareAssembly ||
            d.ImplementationType?.Assembly == MiddlewareAssembly ||
            d.ImplementationInstance?.GetType().Assembly == MiddlewareAssembly);
        Assert.DoesNotContain(await MiddlewaresOf(services), m => m is FluxorViewerMiddleware);
    }

    [Fact]
    public async Task Enabled_reports_the_store_to_the_Viewer_under_the_configured_AppName()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer, v => { v.Enabled = true; v.AppName = "MyApp"; });

        await provider.GetRequiredService<IStore>().InitializeAsync();

        Assert.Equal("MyApp", (await viewer.FirstSnapshot()).AppName);
    }

    [Fact]
    public async Task By_default_it_reports_to_localhost_5299_under_the_entry_assembly_name()
    {
        var viewer = new FakeViewer();
        await using var provider = Provider(viewer, v => v.Enabled = true);

        await provider.GetRequiredService<IStore>().InitializeAsync();

        var snapshot = await viewer.FirstSnapshot();
        Assert.Equal("FluxorViewer.Middleware.Tests", snapshot.AppName); // the entry assembly: here, the test project
        Assert.Equal(new Uri("http://localhost:5299"), Assert.Single(viewer.Connections).ServerUrl);
    }

    [Fact]
    public async Task By_default_1000_Actions_wait_for_the_Viewer_and_older_ones_are_dropped()
    {
        var viewer = FakeViewer.NotUpYet();
        await using var provider = Provider(viewer, v => v.Enabled = true);
        await provider.GetRequiredService<IStore>().InitializeAsync();
        DispatchIncrements(provider, 1000); // after StoreInitializedAction: one more than fits

        viewer.ConnectGate.SetResult();

        Assert.Equal(1, (await viewer.FirstSnapshot()).DroppedCount);
    }

    [Theory]
    [InlineData("http://localhost:5299")]
    [InlineData("https://host.docker.internal:5299")]
    [InlineData("http://127.0.0.1:5299/viewer/")]
    public void Valid_server_urls_are_accepted(string url) =>
        Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.ServerUrl = url; }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:5299")]
    [InlineData("/hub/ingest")]
    [InlineData("ws://localhost:5299")]
    [InlineData("file:///tmp/viewer")]
    public void Invalid_server_url_fails_fast(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.ServerUrl = url; })));

        Assert.Equal("ServerUrl", ex.ParamName);
        Assert.Contains($"'{url}'", ex.Message);
        Assert.Contains("absolute http or https URL", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_app_name_fails_fast(string appName)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.AppName = appName; })));

        Assert.Equal("AppName", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Buffer_size_below_one_fails_fast(int bufferSize)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.BufferSize = bufferSize; })));

        Assert.Equal("BufferSize", ex.ParamName);
        Assert.Contains("at least 1", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void Max_depth_outside_1_to_1024_fails_fast(int maxDepth)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.MaxDepth = maxDepth; })));

        Assert.Equal("MaxDepth", ex.ParamName);
        Assert.Contains("between 1 and 1024", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public void Max_depth_from_1_to_1024_is_accepted(int maxDepth) =>
        Services(o => o.UseFluxorViewer(v => { v.Enabled = true; v.MaxDepth = maxDepth; }));

    [Fact]
    public void All_problems_are_reported_together_on_one_line()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Services(o => o.UseFluxorViewer(v =>
            {
                v.Enabled = true;
                v.ServerUrl = "nope";
                v.AppName = "";
                v.BufferSize = 0;
                v.MaxDepth = 0;
            })));

        Assert.Contains("ServerUrl", ex.Message);
        Assert.Contains("AppName", ex.Message);
        Assert.Contains("BufferSize", ex.Message);
        Assert.Contains("MaxDepth", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact]
    public void Invalid_options_are_ignored_when_disabled() =>
        Services(o => o.UseFluxorViewer(v => { v.ServerUrl = "nope"; v.AppName = ""; v.BufferSize = 0; }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Calling_UseFluxorViewer_twice_is_an_error_whether_enabled_or_not(bool firstEnabled, bool secondEnabled)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Services(o => o
                .UseFluxorViewer(v => v.Enabled = firstEnabled)
                .UseFluxorViewer(v => v.Enabled = secondEnabled)));

        Assert.Contains("already been called", ex.Message);
    }
}
