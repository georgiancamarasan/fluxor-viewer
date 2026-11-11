using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxorViewer.Server.Tests;

// UI-1: the layout shell and Session picker, against a real registry.
public class HomePageTests : BunitContext
{
    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance);

    public HomePageTests() => Services.AddSingleton(registry);

    private static Snapshot NewSnapshot(Guid sessionId, string appName = "MyApp", Runtime runtime = Runtime.WebAssembly) => new(
        ProtocolVersion.Current, sessionId, appName, runtime, DateTimeOffset.UtcNow, 0, 0,
        [new FeatureState("Counter", "App.CounterState", """{"Count":0}""")]);

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

        Assert.True(page.Find("[data-test=feature-picker] select").HasAttribute("disabled"));
        Assert.True(page.Find("[data-test=clear]").HasAttribute("disabled"));
    }
}
