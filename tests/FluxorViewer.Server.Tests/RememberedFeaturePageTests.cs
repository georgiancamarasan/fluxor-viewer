using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace FluxorViewer.Server.Tests;

// UI-13: the Feature picked for each Host app is stored in the browser, so it's selected again after the Viewer tab
// reloads or the Viewer restarts.
public class RememberedFeaturePageTests : BunitContext
{
    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, TimeProvider.System, SessionLimits.Default);

    public RememberedFeaturePageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton<TimeProvider>(new FixedZoneTime());
        JSInterop.SetupPaneModules();
    }

    private readonly Dictionary<string, Guid> sessionIds = [];

    private void Connect(string connectionId, string appName, params string[] features) =>
        registry.SnapshotReceived(connectionId, new Snapshot(
            ProtocolVersion.Current, sessionIds[connectionId] = Guid.NewGuid(), appName, Runtime.WebAssembly, DateTimeOffset.UtcNow, 0, 0,
            [.. features.Select(f => new FeatureState(f, $"App.{f}State", "{}"))]));

    private void SelectSession(IRenderedComponent<Home> page, string connectionId) =>
        page.Find("[data-test=session-picker]").Change(sessionIds[connectionId].ToString());

    private static void SelectFeature(IRenderedComponent<Home> page, string feature) =>
        page.Find("[data-test=feature-picker]").Change(feature);

    private static string[][] Saves(BunitJSModuleInterop picks) =>
        [.. picks.Invocations["saveFeaturePick"].Select(i => i.Arguments.Select(a => (string)a!).ToArray())];

    private static string? SelectedFeature(IRenderedComponent<Home> page) =>
        page.Find("[data-test=feature-picker]").GetAttribute("value");

    [Fact]
    public void The_stored_pick_for_the_Sessions_Host_app_is_selected()
    {
        JSInterop.SetupFeaturePicks(new() { ["ShopApp"] = "Counter", ["AdminApp"] = "Weather" });
        Connect("c1", "ShopApp", "Auth", "Counter", "Weather");

        var page = Render<Home>();

        page.WaitForAssertion(() => Assert.Equal("Counter", SelectedFeature(page)));
    }

    [Fact]
    public void Picking_a_Feature_stores_it_for_the_Sessions_Host_app()
    {
        var picks = JSInterop.SetupFeaturePicks();
        Connect("c1", "ShopApp", "Auth", "Counter");
        var page = Render<Home>();

        SelectFeature(page, "Counter");

        Assert.Equal([["ShopApp", "Counter"]], Saves(picks));
    }

    [Fact]
    public void No_Feature_is_shown_until_the_stored_picks_are_loaded()
    {
        var load = JSInterop.SetupLoadingFeaturePicks();
        Connect("c1", "ShopApp", "Auth", "Counter");

        var page = Render<Home>();

        Assert.Empty(page.FindAll("[data-test=feature-option]"));
        Assert.True(page.Find("[data-test=feature-picker]").HasAttribute("disabled"));

        load.SetResult(new() { ["ShopApp"] = "Counter" });

        page.WaitForAssertion(() => Assert.Equal("Counter", SelectedFeature(page)));
    }

    [Fact]
    public void When_the_stored_picks_cannot_be_loaded_the_first_Feature_is_selected_and_picks_work_in_the_tab()
    {
        JSInterop.SetupLoadingFeaturePicks().SetException(new JSException("Couldn't import the module"));
        Connect("c1", "ShopApp", "Auth", "Counter");
        var page = Render<Home>();
        page.WaitForAssertion(() => Assert.Equal("Auth", SelectedFeature(page)));

        SelectFeature(page, "Counter");

        Assert.Equal("Counter", SelectedFeature(page));
    }

    [Fact]
    public void When_a_pick_cannot_be_stored_it_is_still_kept_in_the_tab()
    {
        JSInterop.SetupFeaturePicks().SetupVoid("saveFeaturePick", _ => true).SetException(new JSException("Couldn't import the module"));
        Connect("c1", "ShopApp", "Auth", "Counter");
        Connect("c2", "AdminApp", "Basket");
        var page = Render<Home>();
        SelectSession(page, "c1");
        SelectFeature(page, "Counter");

        SelectSession(page, "c2");
        SelectSession(page, "c1");

        Assert.Equal("Counter", SelectedFeature(page));
    }
}
