using Fluxor;
using FluxorViewer.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Middleware.Tests;

public class UseFluxorViewerTests
{
    private static readonly System.Reflection.Assembly MiddlewareAssembly = typeof(FluxorViewerOptions).Assembly;

    private static ServiceCollection Services(Action<Fluxor.DependencyInjection.FluxorOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddFluxor(o => configure(o.ScanTypes(typeof(CounterFeature), typeof(CounterReducers))));
        return services;
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
    public async Task Enabled_adds_the_middleware_to_the_store_with_the_configured_options()
    {
        var services = Services(o => o.UseFluxorViewer(v =>
        {
            v.Enabled = true;
            v.AppName = "MyApp";
        }));

        var middleware = Assert.Single((await MiddlewaresOf(services)).OfType<FluxorViewerMiddleware>());
        Assert.Equal("MyApp", middleware.Options.AppName);
    }

    [Fact]
    public void Defaults()
    {
        var options = new FluxorViewerOptions();

        Assert.Equal("http://localhost:5299", options.ServerUrl);
        Assert.Equal(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown", options.AppName);
        Assert.Equal(1000, options.BufferSize);
        Assert.Equal(64, options.MaxDepth);
        Assert.False(options.Enabled);
        Assert.Empty(options.IgnoredActionPredicates);
    }

    [Fact]
    public void IgnoreAction_collects_predicates()
    {
        var options = new FluxorViewerOptions()
            .IgnoreAction(a => a is IncrementAction)
            .IgnoreAction(_ => false);

        Assert.Equal(2, options.IgnoredActionPredicates.Count);
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
    public void All_problems_are_reported_together()
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
    }

    [Fact]
    public void Invalid_options_are_ignored_when_disabled() =>
        Services(o => o.UseFluxorViewer(v => { v.ServerUrl = "nope"; v.AppName = ""; v.BufferSize = 0; }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Calling_twice_throws(bool firstEnabled, bool secondEnabled)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Services(o => o
                .UseFluxorViewer(v => v.Enabled = firstEnabled)
                .UseFluxorViewer(v => v.Enabled = secondEnabled)));

        Assert.Contains("already been called", ex.Message);
    }
}
