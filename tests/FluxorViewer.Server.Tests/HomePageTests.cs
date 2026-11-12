using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxorViewer.Server.Tests;

// UI-1 and UI-2: the layout shell, Session picker and Feature picker, against a real registry.
public class HomePageTests : BunitContext
{
    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance);

    public HomePageTests() => Services.AddSingleton(registry);

    private static Snapshot NewSnapshot(Guid sessionId, string appName = "MyApp", Runtime runtime = Runtime.WebAssembly, params string[] features) => new(
        ProtocolVersion.Current, sessionId, appName, runtime, DateTimeOffset.UtcNow, 0, 0,
        [.. (features is [] ? ["Counter"] : features).Select(f => new FeatureState(f, $"App.{f}State", "{}"))]);

    private static Guid Id(string prefix) => Guid.Parse(prefix + "00000000-0000-0000-0000-000000000000"[prefix.Length..]);

    private string[] Options(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=session-picker] option").Where(o => !o.HasAttribute("disabled")).Select(o => o.TextContent.Trim())];

    [Fact]
    public void With_no_Sessions_it_waits_and_shows_a_setup_snippet_for_this_Viewer()
    {
        var page = Render<Home>();

        var waiting = page.Find("[data-test=waiting]");
        Assert.Contains("Waiting for an app to connect…", waiting.TextContent);
        var snippet = waiting.QuerySelector("pre")!.TextContent;
        Assert.Contains("dotnet add package FluxorViewer.Middleware", snippet);
        Assert.Contains("v.Enabled = builder.Environment.IsDevelopment();", snippet);
        Assert.DoesNotContain("ScanAssemblies", snippet); // a second AddFluxor would register a second store
        Assert.Contains("existing AddFluxor call", waiting.TextContent);
        Assert.Contains(".Client project", waiting.TextContent);
        Assert.Contains("builder.HostEnvironment", waiting.TextContent);
        Assert.Contains("""v.ServerUrl = "http://localhost";""", snippet); // bUnit's base URI
    }

    [Fact]
    public void Sessions_are_labelled_with_their_status_and_listed_newest_first()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("3f2a"), "MyApp", Runtime.WebAssembly));
        registry.SnapshotReceived("c2", NewSnapshot(Id("b7c1"), "MyApp", Runtime.Server));
        registry.ConnectionClosed("c1");

        var page = Render<Home>();

        Assert.Equal(["● MyApp · Server · b7c1", "○ MyApp · WebAssembly · 3f2a"], Options(page));
    }

    [Fact]
    public void A_single_Session_is_selected_and_shown_with_its_status_dot()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("3f2a")));

        var page = Render<Home>();

        Assert.Equal(Id("3f2a").ToString(), page.Find("[data-test=session-picker] select").GetAttribute("value"));
        Assert.NotNull(page.Find("[data-test=status-dot][data-status=connected]"));
        Assert.Empty(page.FindAll("[data-test=pick-a-session]"));
    }

    [Fact]
    public void With_several_Sessions_and_none_picked_the_panes_ask_for_one()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Guid.NewGuid()));
        registry.SnapshotReceived("c2", NewSnapshot(Guid.NewGuid(), "Other"));

        var page = Render<Home>();

        Assert.NotEmpty(page.FindAll("[data-test=pick-a-session]"));
        Assert.Empty(page.FindAll("[data-test=status-dot]"));
    }

    [Fact]
    public void Picking_a_Session_selects_it()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));
        var page = Render<Home>();

        page.Find("[data-test=session-picker] select").Change(Id("bbbb").ToString());

        Assert.Equal(Id("bbbb").ToString(), page.Find("[data-test=session-picker] select").GetAttribute("value"));
        Assert.Empty(page.FindAll("[data-test=pick-a-session]"));
    }

    [Fact]
    public void New_Sessions_status_changes_and_Clears_appear_without_reloading()
    {
        var page = Render<Home>();

        registry.SnapshotReceived("c1", NewSnapshot(Id("3f2a")));
        page.WaitForAssertion(() => Assert.Equal(["● MyApp · WebAssembly · 3f2a"], Options(page)));

        registry.ConnectionClosed("c1");
        page.WaitForAssertion(() => Assert.Equal(["○ MyApp · WebAssembly · 3f2a"], Options(page)));
        Assert.NotNull(page.Find("[data-test=status-dot][data-status=disconnected]"));

        registry.Clear(Id("3f2a"));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-test=waiting]")));
    }

    [Fact]
    public void A_reloaded_app_is_followed()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        var page = Render<Home>();

        registry.ConnectionClosed("c1");
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb")));

        page.WaitForAssertion(() =>
            Assert.Equal(Id("bbbb").ToString(), page.Find("[data-test=session-picker] select").GetAttribute("value")));
    }

    [Fact]
    public void Controls_owned_by_later_stories_are_disabled_placeholders()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Guid.NewGuid()));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=clear]").HasAttribute("disabled"));
    }

    private string[] Features(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=feature-picker] option").Select(o => o.TextContent.Trim())];

    private string? SelectedFeature(IRenderedComponent<Home> page) =>
        page.Find("[data-test=feature-picker] select").GetAttribute("value");

    [Fact]
    public void The_selected_Sessions_Features_are_listed_alphabetically_with_the_first_selected()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Weather", "auth", "Counter"]));

        var page = Render<Home>();

        Assert.Equal(["auth", "Counter", "Weather"], Features(page));
        Assert.Equal("auth", SelectedFeature(page));
        Assert.False(page.Find("[data-test=feature-picker] select").HasAttribute("disabled"));
    }

    [Fact]
    public void Without_a_selected_Session_the_Feature_picker_is_disabled_and_empty()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Guid.NewGuid()));
        registry.SnapshotReceived("c2", NewSnapshot(Guid.NewGuid(), "Other"));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=feature-picker] select").HasAttribute("disabled"));
        Assert.Empty(Features(page));
    }

    [Fact]
    public void A_Session_without_Features_says_so()
    {
        registry.SnapshotReceived("c1", new Snapshot(
            ProtocolVersion.Current, Guid.NewGuid(), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0, []));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=feature-picker] select").HasAttribute("disabled"));
        Assert.Equal(["No Features"], Features(page));
    }

    [Fact]
    public void The_picked_Feature_is_kept_across_Sessions_that_have_it()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other", features: ["Basket"]));
        registry.SnapshotReceived("c3", NewSnapshot(Id("cccc"), "Third", features: ["Counter", "Weather"]));
        var page = Render<Home>();
        page.Find("[data-test=session-picker] select").Change(Id("aaaa").ToString());

        page.Find("[data-test=feature-picker] select").Change("Counter");
        page.Find("[data-test=session-picker] select").Change(Id("bbbb").ToString());
        Assert.Equal("Basket", SelectedFeature(page));

        page.Find("[data-test=session-picker] select").Change(Id("cccc").ToString());
        Assert.Equal("Counter", SelectedFeature(page));
    }

    [Fact]
    public void A_reloaded_app_keeps_the_picked_Feature()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        var page = Render<Home>();
        page.Find("[data-test=feature-picker] select").Change("Counter");

        registry.ConnectionClosed("c1");
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), features: ["Auth", "Counter"]));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(Id("bbbb").ToString(), page.Find("[data-test=session-picker] select").GetAttribute("value"));
            Assert.Equal("Counter", SelectedFeature(page));
        });
    }

    [Fact]
    public void A_Snapshot_that_changes_the_Features_of_a_connected_Session_updates_the_picker()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        var page = Render<Home>();

        // A resync on the same connection raises no Session change; the next Actions batch does.
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter", "Auth"]));
        registry.ActionsReceived("c1", [new ActionRecorded(Id("aaaa"), 1, DateTimeOffset.UtcNow, "App.Tick", "{}", [])]);

        page.WaitForAssertion(() => Assert.Equal(["Auth", "Counter"], Features(page)));
    }
}
