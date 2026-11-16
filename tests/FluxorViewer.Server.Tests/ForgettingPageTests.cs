using AngleSharp.Html.Dom;
using Bunit;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace FluxorViewer.Server.Tests;

// IN-8: what the developer sees once the Viewer has forgotten a disconnected Session, against a real registry.
public class ForgettingPageTests : BunitContext
{
    private SessionRegistry registry = null!;
    private NamedSessions sessions = null!;

    public ForgettingPageTests()
    {
        UseLimit(1);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.SetupPageModules();
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

    private static IHtmlOptionElement? Note(IRenderedComponent<Home> page) =>
        (IHtmlOptionElement?)page.FindAll("[data-test=session-picker] [data-test=forgotten]").SingleOrDefault();

    [Fact]
    public void When_the_selected_Session_is_forgotten_the_selection_empties()
    {
        sessions.Start("first", "second", "third");
        var page = RenderPage();
        SelectSession(page, sessions["first"]);
        sessions.Disconnect("first");

        sessions.Disconnect("second");

        page.WaitForAssertion(() =>
        {
            Assert.Null(SelectedSession(page));
            Assert.NotEmpty(page.FindAll("[data-test=pick-a-session]"));
        });
    }

    [Fact]
    public void Until_a_Session_is_forgotten_the_picker_has_no_note()
    {
        sessions.Start("first", "second");
        sessions.Disconnect("first");

        var page = RenderPage();

        Assert.Null(Note(page));
    }

    [Fact]
    public void Once_a_Session_is_forgotten_the_end_of_the_picker_says_so_with_the_limit_and_is_disabled()
    {
        sessions.Start("first", "second");
        sessions.Disconnect("first");
        var page = RenderPage();

        sessions.Disconnect("second");

        page.WaitForAssertion(() =>
        {
            var note = Note(page)!;
            Assert.Equal("Older disconnected Sessions forgotten · the Viewer keeps the last 1", note.TextContent.Trim());
            Assert.True(note.IsDisabled);
            Assert.Same(note, page.Find("[data-test=session-picker]").LastElementChild);
        });
    }

    [Fact]
    public void With_a_limit_of_0_the_note_says_the_Viewer_keeps_none()
    {
        UseLimit(0);
        sessions.Start("first", "second");
        var page = RenderPage();

        sessions.Disconnect("first");

        page.WaitForAssertion(() =>
            Assert.Equal("Disconnected Sessions forgotten · the Viewer keeps none", Note(page)?.TextContent.Trim()));
    }

    [Fact]
    public void Picking_the_note_leaves_the_selection_as_it_was()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("first", "second");
        var page = RenderPage();
        SelectSession(page, sessions["third"]);

        page.Find("[data-test=session-picker]").Change(Note(page)!.Value);

        Assert.Equal(sessions["third"].ToString(), SelectedSession(page));
    }

    [Fact]
    public void Clearing_a_Session_leaves_the_note()
    {
        sessions.Start("first", "second", "third");
        sessions.Disconnect("first", "second");
        var page = RenderPage();

        registry.Clear(sessions["second"]);

        page.WaitForAssertion(() =>
        {
            Assert.Equal(["third"], sessions.Kept());
            Assert.NotNull(Note(page));
        });
    }
}
