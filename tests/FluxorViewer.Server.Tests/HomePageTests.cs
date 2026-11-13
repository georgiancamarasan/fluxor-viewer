using Bunit;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Components.Pages;
using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace FluxorViewer.Server.Tests;

// UI-1 to UI-8: the layout shell, Session and Feature pickers, Action list, State and Diff tabs, payload strip and Clear, against a real registry.
public class HomePageTests : BunitContext
{
    private readonly SessionRegistry registry = new(NullLogger<SessionRegistry>.Instance, TimeProvider.System, HistoryLimit.Default);

    // The Action list's scrolling, recorded rather than run.
    private readonly BunitJSModuleInterop scroll;

    // The Diff tab's rendering, recorded rather than run: jsondiffpatch runs in the browser.
    private readonly BunitJSModuleInterop diff;

    public HomePageTests()
    {
        Services.AddSingleton(registry);
        Services.AddSingleton<TimeProvider>(new FixedZoneTime());
        scroll = JSInterop.SetupModule("./Components/ActionListPane.razor.js");
        scroll.SetupVoid("scrollToBottom", _ => true);
        diff = JSInterop.SetupModule("./Components/DiffPane.razor.js");
        diff.Setup<bool>("render", _ => true).SetResult(true);
    }

    private static Snapshot NewSnapshot(Guid sessionId, string appName = "MyApp", Runtime runtime = Runtime.WebAssembly, params string[] features) => new(
        ProtocolVersion.Current, sessionId, appName, runtime, DateTimeOffset.UtcNow, 0, 0,
        [.. (features is [] ? ["Counter"] : features).Select(f => new FeatureState(f, $"App.{f}State", "{}"))]);

    private static Guid Id(string prefix) => Guid.Parse(prefix + "00000000-0000-0000-0000-000000000000"[prefix.Length..]);

    private static void SelectSession(IRenderedComponent<Home> page, Guid sessionId) =>
        page.Find("[data-test=session-picker]").Change(sessionId.ToString());

    private static void SelectFeature(IRenderedComponent<Home> page, string feature) =>
        page.Find("[data-test=feature-picker]").Change(feature);

    private static string? SelectedSession(IRenderedComponent<Home> page) =>
        page.Find("[data-test=session-picker]").GetAttribute("value");

    private string[] Options(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=session-option]").Select(o => o.TextContent.Trim())];

    [Fact]
    public void With_no_Sessions_it_waits_and_shows_a_setup_snippet_for_this_Viewer()
    {
        var page = Render<Home>();

        var waiting = page.Find("[data-test=waiting]");
        Assert.Contains("Waiting for a Host app to connect…", waiting.TextContent);
        var snippet = page.Find("[data-test=setup-snippet]").TextContent;
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

        Assert.Equal(Id("3f2a").ToString(), SelectedSession(page));
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

        SelectSession(page, Id("bbbb"));

        Assert.Equal(Id("bbbb").ToString(), SelectedSession(page));
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
            Assert.Equal(Id("bbbb").ToString(), SelectedSession(page)));
    }

    [Fact]
    public void Controls_owned_by_later_stories_are_disabled_placeholders()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Guid.NewGuid()));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=clear]").HasAttribute("disabled"));
    }

    private string[] Features(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=feature-option]").Select(o => o.TextContent.Trim())];

    private string? SelectedFeature(IRenderedComponent<Home> page) =>
        page.Find("[data-test=feature-picker]").GetAttribute("value");

    [Fact]
    public void The_selected_Sessions_Features_are_listed_alphabetically_with_the_first_selected()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Weather", "auth", "Counter"]));

        var page = Render<Home>();

        Assert.Equal(["auth", "Counter", "Weather"], Features(page));
        Assert.Equal("auth", SelectedFeature(page));
        Assert.False(page.Find("[data-test=feature-picker]").HasAttribute("disabled"));
    }

    [Fact]
    public void Without_a_selected_Session_the_Feature_picker_is_disabled_and_empty()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Guid.NewGuid()));
        registry.SnapshotReceived("c2", NewSnapshot(Guid.NewGuid(), "Other"));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=feature-picker]").HasAttribute("disabled"));
        Assert.Empty(Features(page));
    }

    [Fact]
    public void A_Session_without_Features_says_so()
    {
        registry.SnapshotReceived("c1", new Snapshot(
            ProtocolVersion.Current, Guid.NewGuid(), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0, []));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=feature-picker]").HasAttribute("disabled"));
        Assert.Equal(["No Features"], Features(page));
    }

    [Fact]
    public void The_picked_Feature_is_kept_across_Sessions_that_have_it()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other", features: ["Basket"]));
        registry.SnapshotReceived("c3", NewSnapshot(Id("cccc"), "Third", features: ["Counter", "Weather"]));
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));
        SelectFeature(page, "Counter");

        SelectSession(page, Id("bbbb"));
        Assert.Equal("Basket", SelectedFeature(page));

        SelectSession(page, Id("cccc"));
        Assert.Equal("Counter", SelectedFeature(page));
    }

    [Fact]
    public void A_reloaded_app_keeps_the_picked_Feature()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        var page = Render<Home>();
        SelectFeature(page, "Counter");

        registry.ConnectionClosed("c1");
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), features: ["Auth", "Counter"]));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(Id("bbbb").ToString(), SelectedSession(page));
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

    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 2, 3, 45, TimeSpan.Zero);

    private void Dispatch(string connectionId, Guid sessionId, long sequence, string type, params string[] changed) =>
        registry.ActionsReceived(connectionId, [new ActionRecorded(
            sessionId, sequence, At, type, "{}", [.. changed.Select(f => new FeatureState(f, $"App.{f}State", $"{{\"v\":{sequence}}}"))])]);

    private string[] Rows(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=action-row]").Select(r => r.QuerySelector("[data-test=type]")!.TextContent.Trim())];

    [Fact]
    public void Each_Action_row_shows_sequence_short_type_and_local_time()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "MyApp.Items.LoadedAction<MyApp.Item>", "Counter");

        var page = Render<Home>();

        var row = page.Find("[data-test=action-row]");
        Assert.Equal("1", row.QuerySelector("[data-test=sequence]")!.TextContent.Trim());
        var type = row.QuerySelector("[data-test=type]")!;
        Assert.Equal("LoadedAction<Item>", type.TextContent.Trim());
        Assert.Equal("MyApp.Items.LoadedAction<MyApp.Item>", type.GetAttribute("title"));
        var time = row.QuerySelector("[data-test=time]")!;
        Assert.Equal("12:02:03.045", time.TextContent.Trim());
        Assert.Equal("2026-11-12 12:02:03.045 +02:00", time.GetAttribute("title"));
    }

    [Fact]
    public void Actions_that_did_not_change_the_selected_Feature_are_dimmed()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Login", "Auth");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");

        var page = Render<Home>();
        SelectFeature(page, "Counter");

        Assert.Equal(["true", "false"], page.FindAll("[data-test=action-row]").Select(r => r.GetAttribute("data-dimmed")));
    }

    [Fact]
    public void Changed_only_and_the_filter_hide_rows_and_Show_all_brings_them_back()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Login", "Auth");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 3, "App.Decrement", "Counter");
        var page = Render<Home>();
        SelectFeature(page, "Counter");

        page.Find("[data-test=changed-only]").Change(true);
        Assert.Equal(["Increment", "Decrement"], Rows(page));

        page.Find("[data-test=action-filter]").Input("INC");
        Assert.Equal(["Increment"], Rows(page));

        page.Find("[data-test=action-filter]").Input("nothing");
        Assert.Empty(Rows(page));
        Assert.Contains("No Actions match", page.Find("[data-test=no-match]").TextContent);

        page.Find("[data-test=show-all]").Click();
        Assert.Equal(["Login", "Increment", "Decrement"], Rows(page));
        Assert.False(page.Find("[data-test=changed-only]").HasAttribute("checked"));
    }

    [Fact]
    public void Changed_only_is_disabled_without_a_selected_Feature()
    {
        registry.SnapshotReceived("c1", new Snapshot(
            ProtocolVersion.Current, Id("aaaa"), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0, []));

        var page = Render<Home>();

        Assert.True(page.Find("[data-test=changed-only]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_Session_without_Actions_says_so()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));

        var page = Render<Home>();

        Assert.Contains("No Actions yet", page.Find("[data-test=no-actions]").TextContent);
    }

    [Fact]
    public void Clicking_an_older_row_selects_it_and_switching_Session_follows_the_newest_again()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));
        Dispatch("c1", Id("aaaa"), 1, "App.A");
        Dispatch("c1", Id("aaaa"), 2, "App.B");
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));

        page.FindAll("[data-test=action-row]")[0].Click();
        Assert.Equal("A", SelectedRow(page));

        SelectSession(page, Id("bbbb"));
        Assert.Null(SelectedRow(page));

        SelectSession(page, Id("aaaa"));
        Assert.Equal("B", SelectedRow(page));
    }

    [Fact]
    public void New_Actions_appear_without_reloading()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        var page = Render<Home>();

        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");

        page.WaitForAssertion(() => Assert.Equal(["Increment"], Rows(page)));
    }

    [Fact]
    public void Clearing_a_connected_Session_empties_the_list_and_follows_the_next_Action()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.A", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.B", "Counter");
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[0].Click();

        registry.Clear(Id("aaaa"));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-test=no-actions]")));

        Dispatch("c1", Id("aaaa"), 3, "App.C", "Counter");
        page.WaitForAssertion(() => Assert.Equal("C", SelectedRow(page)));
    }

    [Fact]
    public void While_paused_new_Actions_show_a_pill_that_resumes_follow_mode()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.A");
        Dispatch("c1", Id("aaaa"), 2, "App.B");
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[0].Click();

        Dispatch("c1", Id("aaaa"), 3, "App.C");
        Dispatch("c1", Id("aaaa"), 4, "App.D");
        page.WaitForAssertion(() => Assert.Equal("▼ 2 new", page.Find("[data-test=new-actions]").TextContent.Trim()));
        Assert.Equal("A", SelectedRow(page));

        page.Find("[data-test=new-actions]").Click();

        // The rows are Virtualize's to render, which may land just after the click returns.
        page.WaitForAssertion(() => Assert.Equal("D", SelectedRow(page)));
        Assert.Empty(page.FindAll("[data-test=new-actions]"));
    }

    private int Scrolls => scroll.Invocations["scrollToBottom"].Count;

    [Fact]
    public void The_list_scrolls_to_the_newest_Action_only_while_following()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.A");
        Dispatch("c1", Id("aaaa"), 2, "App.B");
        var page = Render<Home>();
        page.WaitForAssertion(() => Assert.Equal(1, Scrolls));

        Dispatch("c1", Id("aaaa"), 3, "App.C");
        page.WaitForAssertion(() => Assert.Equal(2, Scrolls));

        page.FindAll("[data-test=action-row]")[0].Click();
        Assert.Equal(2, Scrolls);

        Dispatch("c1", Id("aaaa"), 4, "App.D");
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-test=new-actions]")));
        Assert.Equal(2, Scrolls);

        page.Find("[data-test=new-actions]").Click();
        page.WaitForAssertion(() => Assert.Equal(3, Scrolls));
    }

    [Fact]
    public void A_burst_of_Actions_renders_a_few_times_and_ends_on_the_newest()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        var page = Render<Home>();

        DispatchIncrements(Id("aaaa"), 5000);

        page.WaitForAssertion(() => Assert.Equal("CounterState · after #5000 Increment", Header(page)));
        // ~10 renders a second: far fewer than one per Action however long the burst took to send.
        Assert.InRange(page.RenderCount, 1, 100);
    }

    /// <summary>Dispatches <paramref name="count"/> increments, one batch each, from Sequence 1.</summary>
    private void DispatchIncrements(Guid sessionId, int count)
    {
        for (var sequence = 1; sequence <= count; sequence++)
            Dispatch("c1", sessionId, sequence, "App.Increment", "Counter");
    }

    private static string? SelectedRow(IRenderedComponent<Home> page) =>
        page.FindAll("[data-test=action-row][aria-selected=true] [data-test=type]").SingleOrDefault()?.TextContent.Trim();

    private static string Header(IRenderedComponent<Home> page) => page.Find("[data-test=state-header]").TextContent.Trim();

    private static string[] Values(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=state] [data-test=value]").Select(v => v.TextContent)];

    [Fact]
    public void The_State_tab_shows_the_selected_Features_state_after_the_selected_Action()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        var page = Render<Home>();

        page.FindAll("[data-test=action-row]")[0].Click();

        Assert.Equal("CounterState · after #1 Increment", Header(page));
        Assert.Equal("App.CounterState", page.Find("[data-test=state-type]").GetAttribute("title"));
        Assert.Equal(["1"], Values(page));
    }

    [Fact]
    public void Without_Actions_it_shows_the_current_state()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));

        var page = Render<Home>();

        Assert.Equal("CounterState · current", Header(page));
    }

    [Fact]
    public void Follow_mode_selects_each_new_Action_and_shows_the_state_after_it()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");

        var page = Render<Home>();
        Assert.Equal("CounterState · after #1 Increment", Header(page));

        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        page.WaitForAssertion(() =>
        {
            Assert.Equal("CounterState · after #2 Increment", Header(page));
            Assert.Equal(["2"], Values(page));
        });
    }

    [Fact]
    public void While_following_the_right_pane_moves_with_the_selection_when_the_filter_or_toggle_changes()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Login", "Auth");
        var page = Render<Home>();
        SelectFeature(page, "Counter");

        page.Find("[data-test=changed-only]").Change(true);
        Assert.Equal("CounterState · after #1 Increment", Header(page));

        page.Find("[data-test=action-filter]").Input("nothing");
        Assert.Equal("CounterState · current", Header(page));

        page.Find("[data-test=show-all]").Click();
        Assert.Equal("CounterState · after #2 Login", Header(page));
    }

    [Fact]
    public void A_Feature_missing_at_the_selected_Action_says_it_did_not_exist_then()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter", "Auth"]));
        Dispatch("c1", Id("aaaa"), 2, "App.Login", "Auth");
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[0].Click();

        SelectFeature(page, "Auth");

        Assert.Equal("Auth didn't exist at this point", page.Find("[data-test=state-missing]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-test=state]"));
    }

    [Fact]
    public void A_state_that_failed_to_serialise_shows_as_an_error_but_other_error_properties_are_data()
    {
        registry.SnapshotReceived("c1", new Snapshot(ProtocolVersion.Current, Id("aaaa"), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
            [new FeatureState("Broken", "App.BrokenState", """{"$error":"Cycle detected"}"""),
             new FeatureState("Odd", "App.OddState", """{"$error":"not really","count":1}""")]));
        var page = Render<Home>();

        SelectFeature(page, "Broken");
        Assert.Equal("Couldn't serialise Broken: Cycle detected", page.Find("[data-test=state-error]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-test=json-tree]"));

        SelectFeature(page, "Odd");
        Assert.Empty(page.FindAll("[data-test=state-error]"));
        Assert.Equal(["\"not really\"", "1"], Values(page));
    }

    /// <summary>An object nested <paramref name="depth"/> levels deep: <c>{"n":{"n":…0…}}</c>.</summary>
    private static string Deep(int depth) => string.Concat(Enumerable.Repeat("""{"n":""", depth)) + "0" + new string('}', depth);

    // MW-4: the middleware may send up to 1024 levels (MaxDepth), past JsonDocument's default of 64.
    [Fact]
    public void A_state_nested_up_to_1024_levels_shows_as_a_tree()
    {
        registry.SnapshotReceived("c1", new Snapshot(ProtocolVersion.Current, Id("aaaa"), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
            [new FeatureState("Deep", "App.DeepState", Deep(1024))]));

        var page = Render<Home>();

        Assert.NotNull(page.Find("[data-test=state] [data-test=json-tree]"));
        Assert.Empty(page.FindAll("[data-test=state] [data-test=unreadable]"));
    }

    [Fact]
    public void A_deep_Payload_can_be_expanded_into_a_tree()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        DispatchPayload(Id("aaaa"), 1, "App.Deep", Deep(100));
        var page = Render<Home>();

        TogglePayload(page);

        Assert.NotNull(page.Find("[data-test=payload-tree] [data-test=json-tree]"));
    }

    [Fact]
    public void A_state_the_Viewer_cant_read_says_so_instead_of_breaking_the_page()
    {
        registry.SnapshotReceived("c1", new Snapshot(ProtocolVersion.Current, Id("aaaa"), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
            [new FeatureState("Deep", "App.DeepState", Deep(1025)), new FeatureState("Torn", "App.TornState", """{"a":""")]));
        var page = Render<Home>();

        SelectFeature(page, "Deep");
        Assert.StartsWith("Couldn't read this state: The maximum configured depth of 1024", page.Find("[data-test=state] [data-test=unreadable]").TextContent.Trim());

        SelectFeature(page, "Torn");
        Assert.StartsWith("Couldn't read this state: ", page.Find("[data-test=state] [data-test=unreadable]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-test=json-tree]"));
    }

    private static Snapshot Nested(Guid sessionId, string appName, params string[] features) => new(
        ProtocolVersion.Current, sessionId, appName, Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
        [.. features.Select(f => new FeatureState(f, $"App.{f}State", """{"a":{"b":{"c":1}}}"""))]);

    private static bool Open(IRenderedComponent<Home> page, string path) =>
        page.FindAll($"[data-test=state] [data-test=toggle][data-path='{path}']") is [var toggle]
        && toggle.GetAttribute("aria-expanded") == "true";

    [Fact]
    public void Expand_and_collapse_choices_belong_to_the_Feature_and_survive_changing_Action_and_Session()
    {
        registry.SnapshotReceived("c1", Nested(Id("aaaa"), "MyApp", "Auth", "Counter"));
        registry.SnapshotReceived("c2", Nested(Id("bbbb"), "Other", "Counter"));
        registry.ActionsReceived("c1", [new ActionRecorded(Id("aaaa"), 1, At, "App.Increment", "{}",
            [new FeatureState("Counter", "App.CounterState", """{"a":{"b":{"c":2}}}""")])]);
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));
        SelectFeature(page, "Counter");

        page.Find("[data-test=state] [data-test=toggle][data-path='$.a.b']").Click();
        Assert.True(Open(page, "$.a.b"));

        SelectFeature(page, "Auth");
        Assert.False(Open(page, "$.a.b"));

        SelectFeature(page, "Counter");
        Assert.True(Open(page, "$.a.b"));

        page.Find("[data-test=action-row]").Click();
        Assert.True(Open(page, "$.a.b"));

        SelectSession(page, Id("bbbb"));
        Assert.True(Open(page, "$.a.b"));
    }

    private static void ShowTab(IRenderedComponent<Home> page, string name) =>
        page.FindAll("[data-test=tab]").Single(t => t.TextContent.Trim() == name).Click();

    private static string SelectedTab(IRenderedComponent<Home> page) =>
        page.Find("[data-test=tab][aria-selected=true]").TextContent.Trim();

    private static string DiffMessage(IRenderedComponent<Home> page) => page.Find("[data-test=diff-message]").TextContent.Trim();

    /// <summary>The Diff tab's header as read on screen, with the markup's line breaks and indents collapsed.</summary>
    private static string DiffHeader(IRenderedComponent<Home> page) =>
        string.Join(' ', page.Find("[data-test=diff-header]").TextContent.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private IReadOnlyList<JSRuntimeInvocation> DiffRenders => diff.Invocations["render"];

    [Fact]
    public void The_State_tab_is_shown_first_and_each_tab_shows_its_own_pane()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");

        var page = Render<Home>();
        Assert.Equal("State", SelectedTab(page));
        Assert.NotEmpty(page.FindAll("[data-test=state]"));

        ShowTab(page, "Diff");
        Assert.Equal("Diff", SelectedTab(page));
        Assert.Empty(page.FindAll("[data-test=state]"));
        Assert.NotEmpty(page.FindAll("[data-test=diff]"));

        ShowTab(page, "State");
        Assert.NotEmpty(page.FindAll("[data-test=state]"));
    }

    [Fact]
    public void The_tab_choice_survives_changing_Action_Feature_and_Session()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), "MyApp", features: ["Auth", "Counter"]));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other", features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));
        ShowTab(page, "Diff");

        page.FindAll("[data-test=action-row]")[0].Click();
        Assert.Equal("Diff", SelectedTab(page));

        SelectFeature(page, "Auth");
        Assert.Equal("Diff", SelectedTab(page));

        SelectSession(page, Id("bbbb"));
        Assert.Equal("Diff", SelectedTab(page));
    }

    [Fact]
    public void The_Diff_tab_sends_before_and_after_to_the_browser_under_a_header()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        var page = Render<Home>();

        ShowTab(page, "Diff");

        Assert.Equal("CounterState · #2 Increment · before → after", DiffHeader(page));
        var render = Assert.Single(DiffRenders);
        Assert.Equal(["""{"v":1}""", """{"v":2}""", false], render.Arguments.Skip(1));
        Assert.Empty(page.FindAll("[data-test=diff-message]"));
    }

    [Fact]
    public void State_the_browser_cannot_read_shows_an_error_in_the_Diff_tab_instead_of_ending_the_page()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        registry.ActionsReceived("c1", [new ActionRecorded(Id("aaaa"), 1, At, "App.Break", "{}",
            [new FeatureState("Counter", "App.CounterState", "{not json")])]);
        diff.Setup<bool>("render", call => Equals(call.Arguments[2], "{not json"))
            .SetException(new JSException("Unexpected token 'n', \"{not json\" is not valid JSON"));
        var page = Render<Home>();

        ShowTab(page, "Diff");

        page.WaitForAssertion(() => Assert.Equal(
            "Couldn't diff Counter at this Action: Unexpected token 'n', \"{not json\" is not valid JSON",
            page.Find("[data-test=diff-error]").TextContent.Trim()));
    }

    [Fact]
    public void Show_unchanged_rediffs_and_is_kept_across_Actions_and_Features()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Auth", "Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter", "Auth");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter", "Auth");
        var page = Render<Home>();

        ShowTab(page, "Diff");
        Assert.False(ShowUnchanged(page));

        page.Find("[data-test=show-unchanged]").Change(true);
        Assert.Equal(true, DiffRenders[^1].Arguments[3]);

        page.FindAll("[data-test=action-row]")[0].Click();
        Assert.True(ShowUnchanged(page));
        Assert.Equal(true, DiffRenders[^1].Arguments[3]);

        SelectFeature(page, "Counter");
        Assert.True(ShowUnchanged(page));
        Assert.Equal(true, DiffRenders[^1].Arguments[3]);
    }

    private static bool ShowUnchanged(IRenderedComponent<Home> page) => page.Find("[data-test=show-unchanged]").HasAttribute("checked");

    [Fact]
    public void The_Diff_tab_explains_when_there_is_no_diff_to_show()
    {
        registry.SnapshotReceived("c1", new Snapshot(ProtocolVersion.Current, Id("aaaa"), "MyApp", Runtime.Server, DateTimeOffset.UtcNow, 0, 0,
            [new FeatureState("Counter", "App.CounterState", "{}"), new FeatureState("Broken", "App.BrokenState", "{}")]));
        var page = Render<Home>();
        ShowTab(page, "Diff");

        SelectFeature(page, "Counter");
        Assert.Equal("Select an Action to see what it changed", DiffMessage(page));

        registry.ActionsReceived("c1", [new ActionRecorded(Id("aaaa"), 1, At, "App.Noop", "{}",
            [new FeatureState("Counter", "App.CounterState", "{}"), new FeatureState("Broken", "App.BrokenState", """{"$error":"Cycle"}""")])]);
        page.WaitForAssertion(() => Assert.Equal("Counter was replaced with an identical state", DiffMessage(page)));
        Assert.Empty(page.FindAll("[data-test=show-unchanged]"));

        SelectFeature(page, "Broken");
        Assert.Equal("Couldn't serialise Broken after this Action: Cycle", page.Find("[data-test=diff-error]").TextContent.Trim());

        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        page.WaitForAssertion(() => Assert.Equal("No change to Broken", DiffMessage(page)));
        Assert.Empty(DiffRenders);
    }

    [Fact]
    public void A_Feature_missing_after_the_selected_Action_says_it_did_not_exist_then()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter", "Auth"]));
        Dispatch("c1", Id("aaaa"), 2, "App.Login", "Auth");
        var page = Render<Home>();
        ShowTab(page, "Diff");
        page.FindAll("[data-test=action-row]")[0].Click();

        SelectFeature(page, "Auth");

        Assert.Equal("Auth didn't exist at this point", DiffMessage(page));
    }

    [Fact]
    public void Differences_the_browser_cannot_see_are_explained()
    {
        diff.Setup<bool>("render", i => i.Arguments[2] is """{"v":1}""").SetResult(false);
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();

        ShowTab(page, "Diff");

        page.WaitForAssertion(() =>
            Assert.Equal("Counter was replaced; the differences are beyond what the diff can show", DiffMessage(page)));
        Assert.Empty(page.FindAll("[data-test=show-unchanged]"));

        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-test=diff-message]")));
        Assert.NotEmpty(page.FindAll("[data-test=show-unchanged]"));
    }

    [Fact]
    public void The_diff_is_recomputed_only_when_the_Action_or_Feature_changes()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa"), features: ["Counter"]));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        var page = Render<Home>();
        ShowTab(page, "Diff");
        page.FindAll("[data-test=action-row]")[0].Click();

        Dispatch("c1", Id("aaaa"), 3, "App.Increment", "Counter");

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-test=new-actions]")));
        Assert.Equal(2, DiffRenders.Count); // #2 when the tab opened, then #1 when clicked: none for #3
    }

    private void DispatchPayload(Guid sessionId, long sequence, string type, string payload) =>
        registry.ActionsReceived("c1", [new ActionRecorded(sessionId, sequence, At, type, payload, [])]);

    private static string PayloadLine(IRenderedComponent<Home> page) => page.Find("[data-test=payload-line]").TextContent;

    private static void TogglePayload(IRenderedComponent<Home> page) => page.Find("[data-test=payload-toggle]").Click();

    [Fact]
    public void The_payload_strip_shows_the_selected_Actions_Payload_on_one_line_above_both_tabs()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        DispatchPayload(Id("aaaa"), 1, "App.Rename", """{"name":"café","count":2}""");

        var page = Render<Home>();
        Assert.Equal("""{"name":"café","count":2}""", PayloadLine(page));
        Assert.Equal("false", page.Find("[data-test=payload-toggle]").GetAttribute("aria-expanded"));
        Assert.Empty(page.FindAll("[data-test=payload-tree]"));

        ShowTab(page, "Diff");
        Assert.Equal("""{"name":"café","count":2}""", PayloadLine(page));
    }

    [Fact]
    public void Without_a_selected_Action_there_is_no_payload_strip()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));

        var page = Render<Home>();

        Assert.Empty(page.FindAll("[data-test=payload]"));
    }

    [Fact]
    public void Expanding_shows_the_Payload_as_a_tree_and_stays_expanded_across_Actions_and_Sessions()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));
        DispatchPayload(Id("aaaa"), 1, "App.Rename", """{"name":"a"}""");
        DispatchPayload(Id("aaaa"), 2, "App.Rename", """{"name":"b"}""");
        registry.ActionsReceived("c2", [new ActionRecorded(Id("bbbb"), 1, At, "App.Other", """{"x":1}""", [])]);
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));

        TogglePayload(page);
        var toggle = page.Find("[data-test=payload-toggle]");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("payload-tree", toggle.GetAttribute("aria-controls"));
        Assert.Equal(["\"b\""], PayloadValues(page));
        Assert.Empty(page.FindAll("[data-test=payload-line]"));

        page.FindAll("[data-test=action-row]")[0].Click();
        Assert.Equal(["\"a\""], PayloadValues(page));

        SelectSession(page, Id("bbbb"));
        Assert.Equal(["1"], PayloadValues(page));

        TogglePayload(page);
        Assert.Empty(page.FindAll("[data-test=payload-tree]"));
        Assert.Equal("""{"x":1}""", PayloadLine(page));
    }

    private static string[] PayloadValues(IRenderedComponent<Home> page) =>
        [.. page.FindAll("[data-test=payload-tree] [data-test=value]").Select(v => v.TextContent)];

    private static bool PayloadOpen(IRenderedComponent<Home> page, string path) =>
        page.FindAll($"[data-test=payload-tree] [data-test=toggle][data-path='{path}']") is [var toggle]
        && toggle.GetAttribute("aria-expanded") == "true";

    [Fact]
    public void The_Payload_trees_expand_and_collapse_choices_belong_to_the_Action_type()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        DispatchPayload(Id("aaaa"), 1, "App.Load", """{"a":{"b":{"c":1}}}""");
        DispatchPayload(Id("aaaa"), 2, "App.Save", """{"a":{"b":{"c":2}}}""");
        DispatchPayload(Id("aaaa"), 3, "App.Load", """{"a":{"b":{"c":3}}}""");
        var page = Render<Home>();
        page.FindAll("[data-test=action-row]")[0].Click();
        TogglePayload(page);

        page.Find("[data-test=payload-tree] [data-test=toggle][data-path='$.a.b']").Click();
        Assert.True(PayloadOpen(page, "$.a.b"));

        page.FindAll("[data-test=action-row]")[1].Click();
        Assert.False(PayloadOpen(page, "$.a.b"));

        page.FindAll("[data-test=action-row]")[2].Click();
        Assert.True(PayloadOpen(page, "$.a.b"));
        Assert.Equal(["3"], PayloadValues(page));
    }

    [Fact]
    public void An_empty_Payload_shows_without_a_toggle()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        DispatchPayload(Id("aaaa"), 1, "App.Reset", "{}");

        var page = Render<Home>();

        Assert.Equal("{}", PayloadLine(page));
        Assert.Empty(page.FindAll("[data-test=payload-toggle]"));
    }

    [Fact]
    public void A_Payload_that_failed_to_serialise_shows_as_an_error_but_other_error_properties_are_data()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        DispatchPayload(Id("aaaa"), 1, "App.Items.LoadAction", """{"$error":"Cycle detected"}""");
        DispatchPayload(Id("aaaa"), 2, "App.Odd", """{"$error":"not really","count":1}""");
        var page = Render<Home>();

        page.FindAll("[data-test=action-row]")[0].Click();
        var error = page.Find("[data-test=payload-error]");
        Assert.Equal("Couldn't serialise LoadAction: Cycle detected", error.TextContent.Trim());
        Assert.Equal("App.Items.LoadAction", error.QuerySelector("[data-test=type]")!.GetAttribute("title"));
        Assert.Empty(page.FindAll("[data-test=payload-toggle]"));

        page.FindAll("[data-test=action-row]")[1].Click();
        Assert.Empty(page.FindAll("[data-test=payload-error]"));
        Assert.Equal("""{"$error":"not really","count":1}""", PayloadLine(page));
    }

    private static void OpenClear(IRenderedComponent<Home> page) => page.Find("[data-test=clear]").Click();

    private static string ClearQuestion(IRenderedComponent<Home> page) => page.Find("[data-test=clear-question]").TextContent;

    private static bool ClearEnabled(IRenderedComponent<Home> page) => !page.Find("[data-test=clear]").HasAttribute("disabled");

    [Fact]
    public void Clear_is_disabled_without_a_selected_Session_or_with_nothing_to_clear()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));

        var page = Render<Home>();
        Assert.False(ClearEnabled(page)); // none picked

        SelectSession(page, Id("aaaa"));
        Assert.False(ClearEnabled(page)); // connected, empty History

        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        page.WaitForAssertion(() => Assert.True(ClearEnabled(page)));

        SelectSession(page, Id("bbbb"));
        Assert.False(ClearEnabled(page)); // connected, empty History

        registry.ConnectionClosed("c2");
        page.WaitForAssertion(() => Assert.True(ClearEnabled(page))); // disconnected, empty History: can be removed
    }

    [Fact]
    public void With_no_Sessions_Clear_is_disabled()
    {
        var page = Render<Home>();

        Assert.False(ClearEnabled(page));
    }

    [Fact]
    public void Clearing_a_connected_Session_empties_its_Action_list_and_keeps_recording()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        var page = Render<Home>();

        OpenClear(page);
        Assert.Equal("Clear 2 Actions from MyApp · aaaa?", ClearQuestion(page));

        page.Find("[data-test=clear-confirm]").Click();
        Assert.Empty(page.FindAll("[data-test=clear-prompt]"));
        Assert.Equal(Id("aaaa").ToString(), SelectedSession(page));
        Assert.NotNull(page.Find("[data-test=no-actions]"));
        var state = page.Find("[role=tabpanel]").TextContent;
        Assert.Contains("· current", state);
        Assert.Contains("2", state); // the current state is kept
        Assert.False(ClearEnabled(page));

        Dispatch("c1", Id("aaaa"), 3, "App.Decrement", "Counter");
        page.WaitForAssertion(() => Assert.Equal(["Decrement"], Rows(page)));
    }

    [Fact]
    public void Clearing_a_disconnected_Session_removes_it()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));
        registry.ConnectionClosed("c1");
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));

        OpenClear(page);
        Assert.Equal("Remove disconnected Session MyApp · aaaa?", ClearQuestion(page));

        page.Find("[data-test=clear-confirm]").Click();
        Assert.Equal(["● Other · WebAssembly · bbbb"], Options(page));
        Assert.Equal(Id("bbbb").ToString(), SelectedSession(page)); // the only one left
    }

    [Fact]
    public void The_prompt_puts_its_text_first_and_focuses_Cancel()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();

        OpenClear(page);

        var prompt = page.Find("[data-test=clear-prompt]");
        Assert.Equal(["clear-question", "clear-confirm", "clear-cancel"], prompt.QuerySelectorAll("[data-test]").Select(c => c.GetAttribute("data-test")));
        JSInterop.VerifyFocusAsyncInvoke();
    }

    [Fact]
    public void Cancel_closes_the_prompt_without_clearing()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();
        OpenClear(page);

        page.Find("[data-test=clear-cancel]").Click();

        Assert.Empty(page.FindAll("[data-test=clear-prompt]"));
        Assert.Equal(["Increment"], Rows(page));
    }

    [Fact]
    public void Escape_closes_the_prompt_without_clearing()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();
        OpenClear(page);

        page.Find("[data-test=clear-prompt]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(page.FindAll("[data-test=clear-prompt]"));
        Assert.Equal(["Increment"], Rows(page));
    }

    [Fact]
    public void The_prompt_follows_the_Sessions_status_and_count_as_they_change()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();

        OpenClear(page);
        Assert.Equal("Clear 1 Action from MyApp · aaaa?", ClearQuestion(page));

        Dispatch("c1", Id("aaaa"), 2, "App.Increment", "Counter");
        page.WaitForAssertion(() => Assert.Equal("Clear 2 Actions from MyApp · aaaa?", ClearQuestion(page)));

        registry.ConnectionClosed("c1");
        page.WaitForAssertion(() => Assert.Equal("Remove disconnected Session MyApp · aaaa?", ClearQuestion(page)));

        page.Find("[data-test=clear-confirm]").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-test=waiting]"))); // the click can queue behind a throttled render
    }

    [Fact]
    public void Selecting_another_Session_cancels_the_prompt()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb"), "Other"));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var page = Render<Home>();
        SelectSession(page, Id("aaaa"));
        OpenClear(page);

        SelectSession(page, Id("bbbb"));

        Assert.Empty(page.FindAll("[data-test=clear-prompt]"));
    }

    [Fact]
    public void Following_a_reloaded_app_cancels_the_prompt()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        registry.ConnectionClosed("c1");
        var page = Render<Home>();
        OpenClear(page);

        registry.SnapshotReceived("c2", NewSnapshot(Id("bbbb")));

        page.WaitForAssertion(() => Assert.Equal(Id("bbbb").ToString(), SelectedSession(page)));
        Assert.Empty(page.FindAll("[data-test=clear-prompt]"));
    }

    [Fact]
    public void A_Clear_in_another_Viewer_tab_shows_here_and_cancels_the_prompt()
    {
        registry.SnapshotReceived("c1", NewSnapshot(Id("aaaa")));
        Dispatch("c1", Id("aaaa"), 1, "App.Increment", "Counter");
        var here = Render<Home>();
        var there = Render<Home>();
        OpenClear(here);
        OpenClear(there);

        there.Find("[data-test=clear-confirm]").Click();

        here.WaitForAssertion(() => Assert.NotNull(here.Find("[data-test=no-actions]")));
        Assert.Empty(here.FindAll("[data-test=clear-prompt]"));
        Assert.False(ClearEnabled(here));
    }
}
