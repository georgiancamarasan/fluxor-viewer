using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FluxorViewer.Server.Tests;

// IN-5: Resyncs in the Action list and the right pane, against a real registry.
public class ResyncPageTests : BunitContext
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 12, 10, 2, 3, 45, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, new FakeTimeProvider(ReceivedAt));

    // The Diff tab's rendering, recorded rather than run: jsondiffpatch runs in the browser.
    private readonly BunitJSModuleInterop diff;

    public ResyncPageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.SetupModule("./Components/ActionListPane.razor.js").SetupVoid("scrollToBottom", _ => true);
        diff = JSInterop.SetupModule("./Components/DiffPane.razor.js");
        diff.Setup<bool>("render", _ => true).SetResult(true);
    }

    private void Snapshot(long lastSequence, long droppedCount = 0, int count = 0) => registry.SnapshotReceived("c1", new Snapshot(
        ProtocolVersion.Current, SessionId, "MyApp", Runtime.Server, ReceivedAt, lastSequence, droppedCount,
        [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{count}}}""")]));

    private void Dispatch(long sequence, string type = "App.Increment") => registry.ActionsReceived("c1", [new ActionRecorded(
        SessionId, sequence, ReceivedAt, type, "{}", [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{sequence}}}""")])]);

    private static string[] RowTexts(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=action-list] [role=option]").Select(r => r.QuerySelector("[data-test=type],[data-test=resync]")!.TextContent.Trim())];

    private static void SelectTab(IRenderedComponent<Home> page, string name) =>
        page.FindAll("[data-test=tab]").Single(t => t.TextContent.Trim() == name).Click();

    [Fact]
    public void A_Resync_row_says_how_many_Actions_were_lost_and_where()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(13, droppedCount: 12);
        Dispatch(14);
        Snapshot(15, droppedCount: 1);

        var page = Render<Home>();

        Assert.Equal(["Increment", "⚠ resync — 12 actions lost", "Increment", "⚠ resync — 1 action lost"], RowTexts(page));
        var resync = page.FindAll("[data-test=resync-row]")[0];
        Assert.Equal("Actions #2–#13 were lost", resync.QuerySelector("[data-test=resync]")!.GetAttribute("title"));
        Assert.Equal(ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff"), resync.QuerySelector("[data-test=time]")!.TextContent.Trim());
        Assert.Equal("Action #15 was lost", page.FindAll("[data-test=resync-row]")[1].QuerySelector("[data-test=resync]")!.GetAttribute("title"));
    }

    [Fact]
    public void A_Resync_from_an_older_Snapshot_has_no_count()
    {
        Snapshot(0);
        Dispatch(1);
        Dispatch(2);
        Snapshot(1, count: 1);

        var page = Render<Home>();

        Assert.Equal("⚠ resync", RowTexts(page)[^1]);
    }

    [Fact]
    public void Actions_sent_again_after_an_older_Snapshot_are_separate_rows_selected_separately()
    {
        Snapshot(0);
        Dispatch(1);
        Dispatch(2, "App.First");
        Snapshot(1, count: 1);
        Dispatch(2, "App.Again");
        var page = Render<Home>();

        Assert.Equal(["Increment", "First", "⚠ resync", "Again"], RowTexts(page));
        page.FindAll("[data-test=action-row]")[1].Click();

        Assert.Equal("true", page.FindAll("[data-test=action-row]")[1].GetAttribute("aria-selected"));
        Assert.Equal("false", page.FindAll("[data-test=action-row]")[2].GetAttribute("aria-selected"));
        Assert.Contains("First", page.Find("[data-test=state-header]").TextContent);
    }

    [Fact]
    public void A_Session_with_only_a_Resync_says_No_Actions_yet_and_shows_the_row()
    {
        Snapshot(40, droppedCount: 12);

        var page = Render<Home>();

        Assert.NotNull(page.Find("[data-test=no-actions]"));
        Assert.Equal(["⚠ resync — 12 actions lost"], RowTexts(page));
    }

    [Fact]
    public void When_no_Action_matches_the_filter_Resync_rows_still_show()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(5, droppedCount: 4);
        var page = Render<Home>();

        page.Find("[data-test=action-filter]").Input("nothing");

        Assert.Contains("No Actions match", page.Find("[data-test=no-match]").TextContent);
        Assert.Equal(["⚠ resync — 4 actions lost"], RowTexts(page));
    }

    [Fact]
    public void A_selected_Resync_shows_the_Snapshot_state_and_no_payload()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(9, droppedCount: 8, count: 9);
        Dispatch(10);
        var page = Render<Home>();

        page.Find("[data-test=resync-row]").Click();

        Assert.Equal("true", page.Find("[data-test=resync-row]").GetAttribute("aria-selected"));
        Assert.Empty(page.FindAll("[data-test=payload]"));
        Assert.Equal("CounterState · after resync", page.Find("[data-test=state-header]").TextContent.Trim());
        Assert.Contains("9", page.Find("[data-test=state] [data-test=json-tree]").TextContent);
    }

    [Fact]
    public void A_selected_Resync_has_no_diff()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(9, droppedCount: 8, count: 9);
        var page = Render<Home>();
        page.Find("[data-test=resync-row]").Click();

        SelectTab(page, "Diff");

        Assert.Equal("History was lost before this point; diff unavailable", page.Find("[data-test=diff-message]").TextContent.Trim());
    }

    [Fact]
    public void Following_onto_a_newest_Resync_shows_the_current_state()
    {
        Snapshot(0);
        Dispatch(1);
        var page = Render<Home>();

        Snapshot(9, droppedCount: 8, count: 9);

        page.WaitForAssertion(() =>
            Assert.Equal("CounterState · after resync", page.Find("[data-test=state-header]").TextContent.Trim()));
    }

    [Fact]
    public void The_lost_range_never_includes_Actions_the_Viewer_received()
    {
        Snapshot(0);
        Dispatch(1);
        Dispatch(2);
        Dispatch(3);
        Snapshot(5, droppedCount: 4, count: 5);

        var page = Render<Home>();

        var resync = page.Find("[data-test=resync]");
        Assert.Equal("⚠ resync — 4 actions lost", resync.TextContent.Trim());
        Assert.Equal("Actions #4–#5 were lost", resync.GetAttribute("title"));
    }

    [Fact]
    public void Show_all_brings_back_the_Actions_around_a_Resync()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(5, droppedCount: 4);
        Dispatch(6);
        var page = Render<Home>();
        page.Find("[data-test=action-filter]").Input("nothing");

        page.Find("[data-test=show-all]").Click();

        Assert.Equal(["Increment", "⚠ resync — 4 actions lost", "Increment"], RowTexts(page));
    }

    [Fact]
    public void The_Action_after_a_Resync_diffs_against_the_Snapshot()
    {
        Snapshot(0);
        Dispatch(1);
        Snapshot(9, droppedCount: 8, count: 9);
        Dispatch(10);
        var page = Render<Home>();

        SelectTab(page, "Diff");

        var render = Assert.Single(diff.Invocations["render"]);
        Assert.Equal(["""{"Count":9}""", """{"Count":10}"""], render.Arguments.Skip(1).Take(2));
    }

    [Fact]
    public void Clearing_a_connected_Session_with_only_a_Resync_asks_to_clear_its_History()
    {
        Snapshot(40, droppedCount: 12);
        var page = Render<Home>();

        page.Find("[data-test=clear]").Click();

        Assert.StartsWith("Clear the History of MyApp · ", page.Find("[data-test=clear-question]").TextContent.Trim());
    }
}
