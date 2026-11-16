using Bunit;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// UI-12: the Clear disconnected button and its inline prompt, against a real registry.
public class ClearDisconnectedPageTests : BunitContext
{
    private SessionRegistry registry = null!;
    private NamedSessions sessions = null!;

    public ClearDisconnectedPageTests()
    {
        UseLimit(10);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.SetupPaneModules();
    }

    private void UseLimit(int disconnected)
    {
        registry = NamedSessions.Registry(disconnected);
        sessions = new NamedSessions(registry);
    }

    private IRenderedComponent<Home> RenderPage()
    {
        Services.AddSingleton(registry);
        return Render<Home>();
    }

    private static void SelectSession(IRenderedComponent<Home> page, Guid sessionId) =>
        page.Find("[data-test=session-picker]").Change(sessionId.ToString());

    private static string? SelectedSession(IRenderedComponent<Home> page) =>
        page.Find("[data-test=session-picker]").GetAttribute("value");

    private static bool Enabled(IRenderedComponent<Home> page) => !page.Find("[data-test=clear-disconnected]").HasAttribute("disabled");

    private static void Open(IRenderedComponent<Home> page) => page.Find("[data-test=clear-disconnected]").Click();

    private static string Question(IRenderedComponent<Home> page) => page.Find("[data-test=clear-disconnected-question]").TextContent;

    private static void Confirm(IRenderedComponent<Home> page) => page.Find("[data-test=clear-disconnected-confirm]").Click();

    private static string?[] TestNames(AngleSharp.Dom.IElement element) =>
        [.. element.QuerySelectorAll("[data-test]").Select(c => c.GetAttribute("data-test"))];

    private static bool Prompting(IRenderedComponent<Home> page) => page.FindAll("[data-test=clear-disconnected-prompt]").Count > 0;

    [Fact]
    public void It_is_disabled_until_a_Session_disconnects_whatever_is_selected()
    {
        sessions.Start("first", "second");
        var page = RenderPage();
        Assert.False(Enabled(page));

        sessions.Disconnect("first");

        page.WaitForAssertion(() => Assert.True(Enabled(page))); // with no Session selected
    }

    [Fact]
    public void The_prompt_counts_the_disconnected_Sessions()
    {
        sessions.Start("first", "second", "connected");
        sessions.Disconnect("first");
        var page = RenderPage();

        Open(page);

        Assert.Equal("Remove 1 disconnected Session?", Question(page));
    }

    [Fact]
    public void Confirming_removes_every_disconnected_Session_and_keeps_the_connected_one_selected()
    {
        sessions.Start("first", "connected", "second");
        sessions.Disconnect("first", "second");
        var page = RenderPage();
        SelectSession(page, sessions["connected"]);
        Open(page);
        Assert.Equal("Remove 2 disconnected Sessions?", Question(page));

        Confirm(page);

        Assert.False(Prompting(page));
        Assert.Equal(["connected"], sessions.Kept());
        Assert.Single(page.FindAll("[data-test=session-option]"));
        Assert.Equal(sessions["connected"].ToString(), SelectedSession(page));
        Assert.False(Enabled(page));
    }

    [Fact]
    public void A_selected_disconnected_Session_that_is_removed_empties_the_selection()
    {
        sessions.Start("gone", "first", "second");
        sessions.Disconnect("gone");
        var page = RenderPage();
        SelectSession(page, sessions["gone"]);
        Open(page);

        Confirm(page);

        Assert.Null(SelectedSession(page));
        Assert.NotEmpty(page.FindAll("[data-test=pick-a-session]"));
    }

    [Fact]
    public void The_prompt_puts_its_text_first_and_focuses_Cancel()
    {
        sessions.Start("gone");
        sessions.Disconnect("gone");
        var page = RenderPage();

        Open(page);

        var prompt = page.Find("[data-test=clear-disconnected-prompt]");
        Assert.Equal(
            ["clear-disconnected-question", "clear-disconnected-confirm", "clear-disconnected-cancel"],
            TestNames(prompt));
        JSInterop.VerifyFocusAsyncInvoke();
    }

    [Fact]
    public void Cancel_closes_the_prompt_without_clearing()
    {
        sessions.Start("gone");
        sessions.Disconnect("gone");
        var page = RenderPage();
        Open(page);

        page.Find("[data-test=clear-disconnected-cancel]").Click();

        Assert.False(Prompting(page));
        Assert.Equal(["gone"], sessions.Kept());
    }

    [Fact]
    public void Escape_closes_the_prompt_without_clearing()
    {
        sessions.Start("gone");
        sessions.Disconnect("gone");
        var page = RenderPage();
        Open(page);

        page.Find("[data-test=clear-disconnected-prompt]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.False(Prompting(page));
        Assert.Equal(["gone"], sessions.Kept());
    }

    [Fact]
    public void The_count_follows_Sessions_that_disconnect_or_reconnect_while_the_prompt_is_open()
    {
        sessions.Start("first", "second");
        sessions.Disconnect("first");
        var page = RenderPage();
        Open(page);

        sessions.Disconnect("second");
        page.WaitForAssertion(() => Assert.Equal("Remove 2 disconnected Sessions?", Question(page)));

        registry.SnapshotReceived("first again", NewSnapshot(sessions["first"]));
        page.WaitForAssertion(() => Assert.Equal("Remove 1 disconnected Session?", Question(page)));

        Confirm(page);
        page.WaitForAssertion(() => Assert.Equal(["first"], sessions.Kept()));
    }

    [Fact]
    public void The_prompt_closes_when_no_Session_is_disconnected_any_more()
    {
        sessions.Start("gone");
        sessions.Disconnect("gone");
        var page = RenderPage();
        Open(page);

        registry.SnapshotReceived("back", NewSnapshot(sessions["gone"]));

        page.WaitForAssertion(() => Assert.False(Prompting(page)));
        Assert.False(Enabled(page));
    }

    [Fact]
    public void The_forgotten_note_stays()
    {
        UseLimit(1);
        sessions.Start("forgotten", "gone", "connected");
        sessions.Disconnect("forgotten", "gone");
        var page = RenderPage();
        Open(page);

        Confirm(page);

        Assert.Equal(["connected"], sessions.Kept());
        Assert.NotNull(page.Find("[data-test=session-picker] [data-test=forgotten]"));
    }

    [Fact]
    public void A_Clear_disconnected_in_another_Viewer_tab_shows_here_and_closes_the_prompt()
    {
        sessions.Start("gone", "connected");
        sessions.Disconnect("gone");
        var here = RenderPage();
        var there = Render<Home>();
        Open(here);
        Open(there);

        Confirm(there);

        here.WaitForAssertion(() => Assert.Single(here.FindAll("[data-test=session-option]")));
        Assert.False(Prompting(here));
    }
}
