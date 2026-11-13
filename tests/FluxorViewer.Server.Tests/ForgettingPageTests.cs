using AngleSharp.Html.Dom;
using Bunit;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-8: what the developer sees once the Viewer has forgotten a disconnected Session, against a real registry.
public class ForgettingPageTests : BunitContext
{
    private readonly SessionRegistry registry = new(
        NullLogger<SessionRegistry>.Instance, TimeProvider.System, SessionLimits.Default with { Disconnected = new DisconnectedSessionLimit(1) });

    public ForgettingPageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.SetupPaneModules();
    }

    /// <summary>Starts a Session reported on a connection named after <paramref name="name"/>.</summary>
    private Guid Start(string name)
    {
        var sessionId = Guid.NewGuid();
        registry.SnapshotReceived(name, NewSnapshot(sessionId));
        return sessionId;
    }

    private void Disconnect(string name) => registry.ConnectionClosed(name);

    private static void SelectSession(IRenderedComponent<Home> page, Guid sessionId) =>
        page.Find("[data-test=session-picker]").Change(sessionId.ToString());

    private static IHtmlOptionElement? Note(IRenderedComponent<Home> page) =>
        (IHtmlOptionElement?)page.FindAll("[data-test=session-picker] [data-test=forgotten]").SingleOrDefault();

    [Fact]
    public void When_the_selected_Session_is_forgotten_the_selection_empties()
    {
        var a = Start("a");
        Start("b");
        Start("c");
        var page = Render<Home>();
        SelectSession(page, a);
        Disconnect("a");

        Disconnect("b");

        page.WaitForAssertion(() =>
        {
            Assert.Null(page.Find("[data-test=session-picker]").GetAttribute("value"));
            Assert.NotEmpty(page.FindAll("[data-test=pick-a-session]"));
        });
    }

    [Fact]
    public void Once_a_Session_is_forgotten_the_end_of_the_picker_says_so_with_the_limit_and_cannot_be_picked()
    {
        Start("a");
        Start("b");
        Disconnect("a");
        var page = Render<Home>();
        Assert.Null(Note(page));

        Disconnect("b");

        page.WaitForAssertion(() =>
        {
            var note = Note(page)!;
            Assert.Equal("Older disconnected Sessions forgotten · the Viewer keeps the last 1", note.TextContent.Trim());
            Assert.True(note.IsDisabled);
            Assert.Same(note, page.Find("[data-test=session-picker]").LastElementChild);
        });
    }
}
