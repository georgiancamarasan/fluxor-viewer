using Bunit;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// UI-9: stepping through the Action list with ↑/↓/Home/End, against a real registry.
public class KeyboardNavigationPageTests : BunitContext
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, TimeProvider.System, SessionLimits.Default);

    private readonly BunitJSModuleInterop scroll;

    public KeyboardNavigationPageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton<TimeProvider>(new FixedZoneTime());
        (scroll, _) = JSInterop.SetupPaneModules();
    }

    private int[] RowsScrolledIntoView =>
        [.. scroll.Invocations["scrollRowIntoView"].Select(call => (int)call.Arguments[1]!)];

    private IRenderedComponent<Home> PageWithIncrements(long count)
    {
        registry.SnapshotReceived("c1", NewSnapshot(SessionId));
        registry.ActionsReceived("c1", Increments(SessionId, 1, count));
        return Render<Home>();
    }

    private IReadOnlyList<JSRuntimeInvocation> StopNativeScrollingCalls => scroll.Invocations["stopNativeScrolling"];

    private static void Press(IRenderedComponent<Home> page, string key) =>
        page.Find("[data-test=action-list]").KeyDown(key);

    private static string? SelectedSequence(IRenderedComponent<Home> page) =>
        page.FindAll("[role=option][aria-selected=true] [data-test=sequence]").SingleOrDefault()?.TextContent.Trim();

    private static string Header(IRenderedComponent<Home> page) => page.Find("[data-test=state-header]").TextContent.Trim();

    [Fact]
    public void Arrow_up_selects_the_previous_row_and_shows_the_state_after_it()
    {
        var page = PageWithIncrements(3);

        Press(page, "ArrowUp");

        page.WaitForAssertion(() => Assert.Equal("2", SelectedSequence(page)));
        Assert.Equal("CounterState · after #2 IncrementAction", Header(page));
    }

    [Fact]
    public void A_keyboard_move_scrolls_its_row_into_view()
    {
        var page = PageWithIncrements(3);

        Press(page, "ArrowUp");

        page.WaitForAssertion(() => Assert.Equal([1], RowsScrolledIntoView));
    }

    [Fact]
    public void A_click_does_not_scroll_its_row_into_view()
    {
        var page = PageWithIncrements(3);

        page.FindAll("[data-test=action-row]")[0].Click();

        page.WaitForAssertion(() => Assert.Equal("1", SelectedSequence(page)));
        Assert.Empty(RowsScrolledIntoView);
    }

    [Fact]
    public void The_list_is_a_tab_stop_and_other_keys_leave_the_selection_alone()
    {
        var page = PageWithIncrements(3);

        Press(page, "Tab");
        Press(page, "PageUp");

        Assert.Equal("0", page.Find("[data-test=action-list]").GetAttribute("tabindex"));
        Assert.Equal("3", SelectedSequence(page));
        Assert.Empty(RowsScrolledIntoView);
    }

    [Fact]
    public void Each_time_the_list_appears_its_navigation_keys_are_kept_from_scrolling_it_natively()
    {
        registry.SnapshotReceived("c1", NewSnapshot(SessionId));
        registry.ActionsReceived("c1", [Increment(SessionId, 1)]);

        var page = Render<Home>();

        page.WaitForAssertion(() => Assert.Single(StopNativeScrollingCalls));

        registry.Clear(SessionId);

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-test=action-list]")));

        registry.ActionsReceived("c1", [Increment(SessionId, 2)]);

        page.WaitForAssertion(() => Assert.Equal(2, StopNativeScrollingCalls.Count));
    }

    [Fact]
    public void A_key_with_nowhere_to_go_scrolls_nothing()
    {
        var page = PageWithIncrements(3);

        Press(page, "ArrowDown");

        Assert.Equal("3", SelectedSequence(page));
        Assert.Empty(RowsScrolledIntoView);
    }
}
