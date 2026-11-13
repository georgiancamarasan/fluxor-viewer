using System.Text.Json;

namespace FluxorViewer.Protocol.Tests;

// IN-1: every protocol message survives the trip through SignalR's JSON hub protocol, which serialises with
// JsonSerializerDefaults.Web.
public class RoundTripTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Web), Web)!;

    [Fact]
    public void Snapshot_round_trips()
    {
        var snapshot = new Snapshot(
            ProtocolVersion.Current,
            Guid.NewGuid(),
            "MyApp",
            Runtime.WebAssembly,
            new DateTimeOffset(2026, 11, 10, 10, 2, 3, TimeSpan.FromHours(2)),
            LastSequence: 41,
            DroppedCount: 12,
            [
                new FeatureState("Counter", "MyApp.CounterState", """{"count":3}"""),
                new FeatureState("Weather", "MyApp.WeatherState", """{"$error":"cycle"}"""),
            ]);

        Assert.Equivalent(snapshot, RoundTrip(snapshot), strict: true);
    }

    [Fact]
    public void ActionRecorded_round_trips()
    {
        var action = new ActionRecorded(
            Guid.NewGuid(),
            Sequence: 42,
            new DateTimeOffset(2026, 11, 10, 8, 2, 4, TimeSpan.Zero),
            "MyApp.IncrementAction",
            """{"amount":1}""",
            [new FeatureState("Counter", "MyApp.CounterState", """{"count":4}""")]);

        Assert.Equivalent(action, RoundTrip(action), strict: true);
    }

    [Fact]
    public void ActionRecorded_with_no_changed_features_round_trips()
    {
        var action = new ActionRecorded(Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, "MyApp.NoOp", "{}", []);

        Assert.Equivalent(action, RoundTrip(action), strict: true);
    }

    [Fact]
    public void Feature_state_JSON_is_carried_as_an_opaque_string()
    {
        var state = new FeatureState("Counter", "MyApp.CounterState", """{"count":3}""");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(state, Web));

        var stateJson = doc.RootElement.GetProperty("stateJson");
        Assert.Equal(JsonValueKind.String, stateJson.ValueKind);
        Assert.Equal("""{"count":3}""", stateJson.GetString());
    }

    [Theory]
    [InlineData(Runtime.Server, "\"Server\"")]
    [InlineData(Runtime.WebAssembly, "\"WebAssembly\"")]
    public void Runtime_is_serialised_by_name(Runtime runtime, string json)
    {
        Assert.Equal(json, JsonSerializer.Serialize(runtime, Web));
        Assert.Equal(runtime, JsonSerializer.Deserialize<Runtime>(json, Web));
    }

    [Fact]
    public void ProtocolVersion_round_trips()
    {
        Assert.Equal(ProtocolVersion.Current, RoundTrip(ProtocolVersion.Current));
    }
}
