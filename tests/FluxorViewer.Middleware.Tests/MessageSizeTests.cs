using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-9: a huge Payload or Feature state doesn't stop a Session reporting: values over MaxValueSize become Serialisation
// errors, batches are split under the Viewer's message limit, and a message still over it is shrunk to fit.
// The Warnings are once per process, so each test that checks them has Action types or Features of its own.
public class MessageSizeTests
{
    private const int Megabyte = 1024 * 1024;

    /// <summary>The message when <paramref name="json"/> is exactly <c>{"$error": "message"}</c>.</summary>
    private static string Error(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("$error", property.Name);
        return property.Value.GetString()!;
    }

    private static readonly Type[] Text = [typeof(TextFeature), typeof(TextReducers)];

    private static readonly Type[] Chapters =
        [typeof(ChapterOneFeature), typeof(ChapterTwoFeature), typeof(ChapterReducers)];

    /// <summary>Text that makes <c>{"Text":"aaa…"}</c>, a Text Payload or Feature state, exactly <paramref name="bytes"/> long.</summary>
    private static string TextOf(int bytes) => new('a', bytes - """{"Text":""}""".Length);

    /// <summary>Dispatches <paramref name="count"/> Notes whose Payloads are <paramref name="bytes"/> long.</summary>
    private static void DispatchNotes(Harness harness, int count, int bytes)
    {
        for (var i = 0; i < count; i++)
            harness.Dispatcher.Dispatch(new NoteAction(TextOf(bytes)));
    }

    /// <summary>How many Actions each batch on <paramref name="connection"/> carried, in order.</summary>
    private static IEnumerable<int> BatchSizes(FakeViewer.FakeConnection connection) => connection.Batches.Select(b => b.Count);

    /// <summary>The level of each entry logged about <paramref name="subject"/>, in order.</summary>
    private static IEnumerable<LogLevel> LevelsLoggedAbout(Harness harness, string subject) =>
        harness.Logs.Entries.Where(e => e.Message.Contains(subject)).Select(e => e.Level);

    [Fact]
    public async Task A_Payload_over_MaxValueSize_is_sent_as_a_Serialisation_error_naming_its_size()
    {
        await using var harness = new Harness(configure: v => v.MaxValueSize = 1024);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new NoteAction(TextOf(3072)));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("Larger than MaxValueSize (1 KB): 3 KB", Error(action.Payload));
    }

    [Fact]
    public async Task A_Payload_under_MaxValueSize_made_of_one_long_string_is_sent_whole()
    {
        await using var harness = new Harness(configure: v => v.MaxValueSize = 1024);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new NoteAction(TextOf(1000)));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(1000, action.Payload.Length);
    }

    [Fact]
    public async Task A_value_over_MaxValueSize_is_logged_at_Warning_the_first_time_and_at_Debug_after()
    {
        await using var harness = new Harness(configure: v => v.MaxValueSize = 1024);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new MemoAction(TextOf(3072)));
        harness.Dispatcher.Dispatch(new MemoAction(TextOf(3072)));

        await Harness.ActionsSent(connection, 2);
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], LevelsLoggedAbout(harness, "MemoAction"));
    }

    [Fact]
    public async Task By_default_a_Payload_or_Feature_state_over_8_MB_is_sent_as_a_Serialisation_error()
    {
        await using var harness = new Harness(types: Text);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new WriteAction(TextOf(13_002_342))); // 12.4 MB

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("Larger than MaxValueSize (8 MB): 12.4 MB", Error(Assert.Single(action.ChangedFeatures).StateJson));
        Assert.StartsWith("Larger than MaxValueSize (8 MB): ", Error(action.Payload));
    }

    [Fact]
    public async Task Actions_waiting_together_are_split_into_batches_under_the_Viewers_32_MB_limit()
    {
        await using var harness = new Harness();
        var connection = await harness.Start();
        DispatchNotes(harness, 10, 5 * Megabyte);

        harness.Viewer.ConnectGate.SetResult();

        await Harness.ActionsSent(connection, 10);
        // StoreInitializedAction and six 5 MB Notes fill the first batch; the other four Notes go in the second.
        Assert.Equal([7, 4], BatchSizes(connection));
    }

    [Fact]
    public async Task A_Snapshot_over_the_limit_leaves_out_its_largest_Feature_states_until_it_fits_and_the_Session_goes_on()
    {
        await using var harness = new Harness(types: Chapters, configure: v => { v.BufferSize = 1; v.MaxValueSize = 32 * Megabyte; });
        var connection = await harness.Start();
        harness.Dispatcher.Dispatch(new WriteChapterOneAction(TextOf(20 * Megabyte)));
        harness.Dispatcher.Dispatch(new WriteChapterTwoAction(TextOf(15 * Megabyte)));
        harness.Dispatcher.Dispatch(new NothingAction()); // both writes are dropped into the base

        harness.Viewer.ConnectGate.SetResult();

        var snapshot = await harness.Viewer.FirstSnapshot();
        Assert.Equal("Left out to stay under the Viewer's 32 MB limit (20 MB)",
            Error(Assert.Single(snapshot.Features, f => f.Name == "ChapterOne").StateJson));
        Assert.Equal(15 * Megabyte, Assert.Single(snapshot.Features, f => f.Name == "ChapterTwo").StateJson.Length);
        Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Single(harness.Viewer.Connections);
        Assert.Equal([LogLevel.Warning], LevelsLoggedAbout(harness, "Feature ChapterOne"));
    }

    [Fact]
    public async Task An_Action_over_the_limit_leaves_out_its_largest_value_with_its_Payload_among_them()
    {
        await using var harness = new Harness(types: Chapters, configure: v => v.MaxValueSize = 32 * Megabyte);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new ReviseChapterOneAction(TextOf(20 * Megabyte), new string('n', 5 * Megabyte)));
        harness.Dispatcher.Dispatch(new ReviseChapterOneAction(TextOf(20 * Megabyte), new string('n', 5 * Megabyte)));

        var actions = await Harness.ActionsSent(connection, 2);
        Assert.Equal("Left out to stay under the Viewer's 32 MB limit (25 MB)", Error(actions[0].Payload));
        Assert.Equal(20 * Megabyte, Assert.Single(actions[0].ChangedFeatures).StateJson.Length);
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], LevelsLoggedAbout(harness, "ReviseChapterOneAction"));
    }

    [Fact]
    public async Task After_an_Action_is_shrunk_the_next_Snapshot_still_has_the_real_Feature_state()
    {
        await using var harness = new Harness(types: Chapters, configure: v => v.MaxValueSize = 32 * Megabyte);
        var (connection, _) = await harness.StartConnected();
        harness.Dispatcher.Dispatch(new ReviseChapterTwoAction(TextOf(20 * Megabyte), new string('n', 5 * Megabyte)));
        await Harness.ActionsSent(connection, 1);

        connection.Close();
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);

        var next = Assert.IsType<Snapshot>(await harness.Viewer.Connections.Last().FirstMessage.WaitAsync(Wait.Timeout, Wait.CancellationToken));
        Assert.Equal(20 * Megabyte, Assert.Single(next.Features, f => f.Name == "ChapterTwo").StateJson.Length);
    }

    [Fact]
    public async Task A_Snapshot_still_over_the_limit_with_every_Feature_state_left_out_stops_the_Session_instead_of_looping()
    {
        await using var harness = new Harness(types: [typeof(LongNamedFeature)]);
        var connection = await harness.Start();

        harness.Viewer.ConnectGate.SetResult();

        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Message.Contains("could not build the Snapshot")));
        Assert.Empty(connection.Sent);
        Assert.Single(harness.Viewer.Connections);
    }
}

public record NoteAction(string Text);

public record MemoAction(string Text);

/// <summary>Replaces the Text Feature's state with <paramref name="Text"/>.</summary>
public record WriteAction(string Text);

public record TextState(string Text);

public class TextFeature : Feature<TextState>
{
    public override string GetName() => "Text";

    protected override TextState GetInitialState() => new("");
}

public static class TextReducers
{
    [ReducerMethod]
    public static TextState Write(TextState _, WriteAction action) => new(action.Text);
}

public record ChapterOneState(string Text);

public record ChapterTwoState(string Text);

public record WriteChapterOneAction(string Text);

public record WriteChapterTwoAction(string Text);

/// <summary>Replaces ChapterOne's text with <paramref name="Text"/>; the <paramref name="Notes"/> only add to its Payload.</summary>
public record ReviseChapterOneAction(string Text, string Notes);

/// <summary>Replaces ChapterTwo's text with <paramref name="Text"/>; the <paramref name="Notes"/> only add to its Payload.</summary>
public record ReviseChapterTwoAction(string Text, string Notes);

public class ChapterOneFeature : Feature<ChapterOneState>
{
    public override string GetName() => "ChapterOne";

    protected override ChapterOneState GetInitialState() => new("");
}

public class ChapterTwoFeature : Feature<ChapterTwoState>
{
    public override string GetName() => "ChapterTwo";

    protected override ChapterTwoState GetInitialState() => new("");
}

public static class ChapterReducers
{
    [ReducerMethod]
    public static ChapterOneState Write(ChapterOneState _, WriteChapterOneAction action) => new(action.Text);

    [ReducerMethod]
    public static ChapterTwoState Write(ChapterTwoState _, WriteChapterTwoAction action) => new(action.Text);

    [ReducerMethod]
    public static ChapterOneState Revise(ChapterOneState _, ReviseChapterOneAction action) => new(action.Text);

    [ReducerMethod]
    public static ChapterTwoState Revise(ChapterTwoState _, ReviseChapterTwoAction action) => new(action.Text);
}

public record LongNamedState;

/// <summary>A Feature whose name alone, at 33 MB, is over the Viewer's limit, so no Snapshot can fit.</summary>
public class LongNamedFeature : Feature<LongNamedState>
{
    private static readonly string Name = new('n', 33 * 1024 * 1024);

    public override string GetName() => Name;

    protected override LongNamedState GetInitialState() => new();
}
