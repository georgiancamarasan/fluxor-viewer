using System.Text.Json;
using System.Text.Json.Serialization;
using Fluxor;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware.Tests;

// MW-11: reporting a large Payload or Feature state costs the Host app about its own size: a long string takes no more
// than the same characters in short pieces, and a value over MaxValueSize is measured without being held in full.
// Memory is measured process-wide, so this class runs alone (ReportingMemory collection), and each test compares two
// values that cost the rest of the pipeline the same, rather than holding one to an absolute bound. A value is also
// serialised only once, even one whose own converter writes a long string itself.
[Collection(nameof(ReportingMemory))]
public class ReportingMemoryTests
{
    private const int Million = 1_000_000;
    private const int Megabyte = 1024 * 1024;

    /// <summary>How much more one long string may cost than the same characters in pieces.</summary>
    private const double Allowance = 1.5;

    /// <summary>
    /// The bytes allocated, process-wide, from dispatching <paramref name="action"/> until the Viewer has it, the only
    /// Action the test has dispatched since the Session started, or the one after <paramref name="sentBefore"/>.
    /// </summary>
    private static async Task<long> AllocatedReporting(Harness harness, FakeViewer.FakeConnection connection, object action,
        int sentBefore = 0)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        harness.Dispatcher.Dispatch(action);
        await Harness.ActionsSent(connection, sentBefore + 1);
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>The message when <paramref name="json"/> is exactly <c>{"$error": "message"}</c>.</summary>
    private static string Error(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("$error", property.Name);
        return property.Value.GetString()!;
    }

    /// <summary><paramref name="text"/> cut into <paramref name="pieces"/> strings of equal length.</summary>
    private static List<string> InPieces(string text, int pieces) =>
        text.Chunk(text.Length / pieces).Select(chunk => new string(chunk)).ToList();

    [Fact]
    public async Task A_Payload_of_one_long_string_needing_escaping_costs_about_what_the_same_characters_in_pieces_do()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();
        var quotes = new string('"', Million); // 6 MB of JSON, each " escaped to "
        var inPieces = new PiecesAction(InPieces(quotes, 1000));
        var whole = new WholeAction(quotes);

        var piecesCost = await AllocatedReporting(harness, connection, inPieces);
        var wholeCost = await AllocatedReporting(harness, connection, whole, sentBefore: 1);

        Assert.InRange(wholeCost, 0, piecesCost * Allowance);
    }

    [Fact]
    public async Task A_Feature_state_of_one_long_non_ASCII_string_costs_about_what_the_same_characters_in_pieces_do()
    {
        await using var harness = new Harness(types: Accents);
        var (connection, _) = await harness.StartConnected();
        var inPieces = new WriteAccentsInPiecesAction(Million, Pieces: 1000); // 6 MB of JSON, each é escaped to é
        var whole = new WriteAccentsAction(Million);

        var piecesCost = await AllocatedReporting(harness, connection, inPieces);
        var wholeCost = await AllocatedReporting(harness, connection, whole, sentBefore: 1);

        Assert.InRange(wholeCost, 0, piecesCost * Allowance);
    }

    [Fact]
    public async Task A_Payload_whose_own_converter_writes_one_long_string_is_serialised_once_and_sent_whole()
    {
        await using var harness = new Harness();
        var (connection, _) = await harness.StartConnected();

        harness.Dispatcher.Dispatch(new SelfWrittenAction(new string('a', 3 * Million)));

        var action = Assert.Single(await Harness.ActionsSent(connection, 1));
        Assert.Equal(1, SelfWrittenConverter.Writes);
        Assert.Equal(3 * Million + 2, action.Payload.Length); // the string, in quotes
    }

    [Fact]
    public async Task A_Payload_over_MaxValueSize_costs_no_more_however_far_over_it_is()
    {
        await using var harness = new Harness(configure: v => v.MaxValueSize = Megabyte);
        var (connection, _) = await harness.StartConnected();
        var twoMegabytes = new WholeAction(new string('"', Million / 3)); // each " escaped to \u0022, 6 bytes
        var twelveMegabytes = new WholeAction(new string('"', 2 * Million));

        var twoMegabytesCost = await AllocatedReporting(harness, connection, twoMegabytes);
        var twelveMegabytesCost = await AllocatedReporting(harness, connection, twelveMegabytes, sentBefore: 1);

        Assert.InRange(twelveMegabytesCost, 0, twoMegabytesCost * Allowance);
        Assert.All(await Harness.ActionsSent(connection, 2),
            action => Assert.StartsWith("Larger than MaxValueSize (1 MB): ", Error(action.Payload)));
    }

    private static readonly Type[] Accents = [typeof(AccentsFeature), typeof(AccentsReducers)];
}

/// <summary>Runs the memory tests alone, so no other test's allocations are counted.</summary>
[CollectionDefinition(nameof(ReportingMemory), DisableParallelization = true)]
public class ReportingMemory;

public record WholeAction(string Text);

public record PiecesAction(List<string> Pieces);

/// <summary>Replaces the Accents Feature's state with one string of <paramref name="Length"/> é.</summary>
public record WriteAccentsAction(int Length);

/// <summary>Replaces the Accents Feature's state with <paramref name="Length"/> é cut into <paramref name="Pieces"/> strings.</summary>
public record WriteAccentsInPiecesAction(int Length, int Pieces);

public record AccentsState(string Whole, List<string> Pieces);

public class AccentsFeature : Feature<AccentsState>
{
    public override string GetName() => "Accents";

    protected override AccentsState GetInitialState() => new("", []);
}

public static class AccentsReducers
{
    [ReducerMethod]
    public static AccentsState Write(AccentsState _, WriteAccentsAction action) => new(new string('é', action.Length), []);

    [ReducerMethod]
    public static AccentsState Write(AccentsState _, WriteAccentsInPiecesAction action) =>
        new("", Enumerable.Range(0, action.Pieces).Select(_ => new string('é', action.Length / action.Pieces)).ToList());
}

/// <summary>An Action written by its own converter, as one string, rather than by the middleware's.</summary>
[JsonConverter(typeof(SelfWrittenConverter))]
public record SelfWrittenAction(string Text);

/// <summary>Writes a <see cref="SelfWrittenAction"/> as its text in one call, counting how often it's asked to.</summary>
public sealed class SelfWrittenConverter : JsonConverter<SelfWrittenAction>
{
    private static int writes;

    /// <summary>How many times a <see cref="SelfWrittenAction"/> has been written.</summary>
    public static int Writes => writes;

    public override SelfWrittenAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SelfWrittenAction value, JsonSerializerOptions options)
    {
        Interlocked.Increment(ref writes);
        writer.WriteStringValue(value.Text);
    }
}
