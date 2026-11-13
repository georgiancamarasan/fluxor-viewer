using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-6: what the developer sees once a Session's History has gone past its limit, against a real registry.
public class HistoryLimitPageTests : BunitContext
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, TimeProvider.System, new HistoryLimit(3));

    public HistoryLimitPageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton(TimeProvider.System);
        JSInterop.SetupPaneModules();
    }

    private static FeatureState Weather(long day) => new("Weather", "App.WeatherState", $$"""{"Day":{{day}}}""");

    private void Snapshot() => registry.SnapshotReceived("c1", NewSnapshot(SessionId) with { Features = [Counter(0), Weather(0)] });

    private void Dispatch(params long[] sequences) => registry.ActionsReceived("c1", [.. sequences.Select(s => Increment(SessionId, s))]);

    // Actions that change only Weather, so "changed only" hides them while Counter (the first Feature) is selected.
    private void DispatchWeather(params long[] sequences) => registry.ActionsReceived("c1",
        [.. sequences.Select(s => Increment(SessionId, s) with { ActionType = "App.NextDay", ChangedFeatures = [Weather(s)] })]);

    private static string? Note(IRenderedComponent<Home> page) =>
        page.FindAll("[data-test=discarded]").SingleOrDefault()?.TextContent.Trim();

    private static long[] Sequences(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=action-list] [role=option] [data-test=sequence]").Select(s => long.Parse(s.TextContent))];

    [Fact]
    public void Once_older_entries_are_discarded_a_note_above_the_list_says_so_with_the_limit()
    {
        Snapshot();
        Dispatch(1, 2, 3);

        var page = Render<Home>();
        Assert.Null(Note(page));

        Dispatch(4);

        page.WaitForAssertion(() =>
        {
            Assert.Equal("Older entries discarded · the Viewer keeps the last 3 per Session", Note(page));
            Assert.Equal([2, 3, 4], Sequences(page));
        });
    }

    [Fact]
    public void The_filter_never_hides_the_note()
    {
        Snapshot();
        Dispatch(1, 2, 3, 4);
        var page = Render<Home>();

        page.Find("[data-test=action-filter]").Input("nothing matches this");

        Assert.NotNull(Note(page));
        Assert.NotNull(page.Find("[data-test=no-match]"));
    }

    [Fact]
    public void Discarding_the_selected_Action_resumes_Follow_mode_and_the_note_is_never_selected()
    {
        Snapshot();
        Dispatch(1, 2, 3);
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[0].Click(); // #1: pauses Follow mode

        Dispatch(4, 5);

        page.WaitForAssertion(() =>
        {
            Assert.Equal([3, 4, 5], Sequences(page));
            var selected = page.Find("[aria-selected=true] [data-test=sequence]");
            Assert.Equal("5", selected.TextContent.Trim());
            Assert.Empty(page.FindAll("[data-test=new-actions]"));
            Assert.NotNull(Note(page));
        });
    }

    [Fact]
    public void Clearing_a_connected_Session_removes_the_note_until_the_limit_is_passed_again()
    {
        Snapshot();
        Dispatch(1, 2, 3, 4);

        var page = Render<Home>();
        Assert.NotNull(Note(page));

        registry.Clear(SessionId);
        page.WaitForAssertion(() => Assert.Null(Note(page)));

        Dispatch(5, 6, 7);
        page.WaitForAssertion(() => Assert.Equal([5, 6, 7], Sequences(page)));
        Assert.Null(Note(page));

        Dispatch(8);
        page.WaitForAssertion(() => Assert.NotNull(Note(page)));
    }

    [Fact]
    public void Changed_only_never_hides_the_note()
    {
        Snapshot();
        DispatchWeather(1, 2, 3, 4);
        var page = Render<Home>();

        page.Find("[data-test=changed-only]").Change(true);

        Assert.Empty(page.FindAll("[data-test=action-list] [role=option]"));
        Assert.NotNull(page.Find("[data-test=no-match]"));
        Assert.NotNull(Note(page));
    }

    [Fact]
    public void While_paused_discarding_older_rows_keeps_the_selection_and_the_new_count_ignores_the_note()
    {
        Snapshot();
        Dispatch(1, 2, 3);
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[1].Click(); // #2: pauses Follow mode

        Dispatch(4);

        page.WaitForAssertion(() =>
        {
            Assert.Equal([2, 3, 4], Sequences(page));
            Assert.NotNull(Note(page));
            Assert.Equal("2", page.Find("[aria-selected=true] [data-test=sequence]").TextContent.Trim());
            Assert.Equal("▼ 1 new", page.Find("[data-test=new-actions]").TextContent.Trim());
        });
    }
}
