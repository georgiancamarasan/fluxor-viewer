using System.Text.Json;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// MW-9: a huge Payload or Feature state doesn't stop a Session reporting: values over MaxValueSize become Serialisation
// errors, batches are split under the Viewer's message limit, and a message still over it is shrunk to fit.
public class MessageSizeTests
{
    /// <summary>The message when <paramref name="json"/> is exactly <c>{"$error": "message"}</c>.</summary>
    private static string Error(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("$error", property.Name);
        return property.Value.GetString()!;
    }

    private static readonly Type[] Text = [typeof(TextFeature), typeof(TextReducers)];

    /// <summary>Text whose Feature state, <c>{"Text":"aaa…"}</c>, is exactly <paramref name="bytes"/> long.</summary>
    private static string TextOf(int bytes) => new('a', bytes - """{"Text":""}""".Length);

    private static readonly Type[] Chapters =
        [typeof(ChapterOneFeature), typeof(ChapterTwoFeature), typeof(ChapterReducers)];

    /// <summary>A Note whose Payload, <c>{"Text":"aaa…"}</c>, is exactly <paramref name="bytes"/> long.</summary>
    private static NoteAction NoteOf(int bytes) => new(new string('a', bytes - """{"Text":""}""".Length));

    [Fact]
    public async Task A_Payload_over_MaxValueSize_is_sent_as_a_Serialisation_error_naming_its_size()
    {
        await using var harness = new Harness(configure: v => v.MaxValueSize = 1024);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(NoteOf(3072));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("Larger than MaxValueSize (1 KB): 3 KB", Error(action.Payload));
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
        for (var i = 0; i < 10; i++)
            harness.Dispatcher.Dispatch(NoteOf(5 * 1024 * 1024));

        harness.Viewer.ConnectGate.SetResult();

        await Harness.ActionsSent(connection, 10);
        Assert.Equal([7, 4], connection.Batches.Select(b => b.Count)); // StoreInitializedAction and six 5 MB Notes; four
    }

    [Fact]
    public async Task A_Snapshot_over_the_limit_leaves_out_its_largest_Feature_states_until_it_fits_and_the_Session_goes_on()
    {
        await using var harness = new Harness(types: Chapters, configure: v => { v.BufferSize = 1; v.MaxValueSize = 32 * 1024 * 1024; });
        var connection = await harness.Start();
        harness.Dispatcher.Dispatch(new WriteChapterOneAction(TextOf(20 * 1024 * 1024)));
        harness.Dispatcher.Dispatch(new WriteChapterTwoAction(TextOf(15 * 1024 * 1024)));
        harness.Dispatcher.Dispatch(new NothingAction()); // both writes are dropped into the base

        harness.Viewer.ConnectGate.SetResult();

        var snapshot = await harness.Viewer.FirstSnapshot();
        Assert.Equal("Left out to stay under the Viewer's 32 MB limit (20 MB)",
            Error(Assert.Single(snapshot.Features, f => f.Name == "ChapterOne").StateJson));
        Assert.Equal(15 * 1024 * 1024, Assert.Single(snapshot.Features, f => f.Name == "ChapterTwo").StateJson.Length);
        Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Single(harness.Viewer.Connections);
        var warning = Assert.Single(harness.Logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("ChapterOne", warning.Message);
    }

    [Fact]
    public async Task An_Action_over_the_limit_leaves_out_its_largest_value_and_the_next_Snapshot_has_the_real_state()
    {
        await using var harness = new Harness(types: Chapters, configure: v => v.MaxValueSize = 32 * 1024 * 1024);
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new ReviseChapterOneAction(TextOf(20 * 1024 * 1024), new string('n', 5 * 1024 * 1024)));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal("Left out to stay under the Viewer's 32 MB limit (25 MB)", Error(action.Payload));
        Assert.Equal(20 * 1024 * 1024, Assert.Single(action.ChangedFeatures).StateJson.Length);
        Assert.Contains("ReviseChapterOneAction", Assert.Single(harness.Logs.Entries, e => e.Level == LogLevel.Warning).Message);

        connection.Close();
        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 2);

        var next = Assert.IsType<Snapshot>(await harness.Viewer.Connections.Last().FirstMessage.WaitAsync(Wait.Timeout, Wait.CancellationToken));
        Assert.Equal(20 * 1024 * 1024, Assert.Single(next.Features, f => f.Name == "ChapterOne").StateJson.Length);
    }
}

public record NoteAction(string Text);

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
}
