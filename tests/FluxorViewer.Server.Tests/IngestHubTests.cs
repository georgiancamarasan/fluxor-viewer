using System.Net;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Ingest;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Server.Tests;

// IN-2: the hub accepts Snapshots and Actions from middleware and enforces the protocol.
public class IngestHubTests(Viewer viewer) : IClassFixture<Viewer>
{
    private static CancellationToken Ct => Wait.Ct;

    private static Snapshot NewSnapshot(Guid? sessionId = null, ProtocolVersion? version = null, IReadOnlyList<FeatureState>? features = null) => new(
        version ?? ProtocolVersion.Current,
        sessionId ?? Guid.NewGuid(),
        "TestApp",
        Runtime.WebAssembly,
        DateTimeOffset.UtcNow,
        LastSequence: 0,
        DroppedCount: 0,
        features ?? [new FeatureState("Counter", "App.CounterState", """{"Count":0}""")]);

    private static ActionRecorded NewAction(Guid sessionId, long sequence) => new(
        sessionId, sequence, DateTimeOffset.UtcNow, "App.IncrementAction", "{}",
        [new FeatureState("Counter", "App.CounterState", $$"""{"Count":{{sequence}}}""")]);

    [Fact]
    public async Task A_Snapshot_and_three_Actions_reach_the_registry()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot();

        await connection.InvokeAsync("Snapshot", snapshot, Ct);
        await connection.InvokeAsync("Actions", new[] { NewAction(snapshot.SessionId, 1), NewAction(snapshot.SessionId, 2) }, Ct);
        await connection.InvokeAsync("Actions", new[] { NewAction(snapshot.SessionId, 3) }, Ct);

        var session = viewer.Registry.Find(snapshot.SessionId);
        Assert.NotNull(session);
        Assert.Equal("TestApp", session.AppName);
        Assert.Equal(Runtime.WebAssembly, session.Runtime);
        Assert.Equal(connection.ConnectionId, session.ConnectionId);
        Assert.Equal(snapshot, Assert.Single(session.Snapshots), SnapshotComparer.Instance);
        Assert.Equal([1L, 2L, 3L], session.Actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":3}""", session.Actions[2].ChangedFeatures[0].StateJson);
    }

    [Fact]
    public async Task A_later_Snapshot_for_the_same_Session_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Ct);
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { LastSequence = 40, DroppedCount = 12 }, Ct);

        Assert.Equal([0L, 40L], viewer.Registry.Find(sessionId)!.Snapshots.Select(s => s.LastSequence));
    }

    [Fact]
    public async Task A_different_minor_version_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(version: ProtocolVersion.Current with { Minor = ProtocolVersion.Current.Minor + 7 });

        await connection.InvokeAsync("Snapshot", snapshot, Ct);

        Assert.NotNull(viewer.Registry.Find(snapshot.SessionId));
    }

    [Fact]
    public async Task An_incompatible_major_version_is_rejected_and_logged_once_per_Session()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(version: new ProtocolVersion(ProtocolVersion.Current.Major + 1, 0));

        var first = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Ct));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Ct));
        await using var retry = await viewer.ConnectAsync();
        await Assert.ThrowsAsync<HubException>(() => retry.InvokeAsync("Snapshot", snapshot, Ct));

        Assert.Contains($"protocol version {snapshot.ProtocolVersion} is not compatible", first.Message);
        Assert.Null(viewer.Registry.Find(snapshot.SessionId));
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), l => l.Message.Contains(snapshot.SessionId.ToString()));
    }

    [Fact]
    public async Task Actions_before_a_Snapshot_are_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Actions", new[] { NewAction(sessionId, 1) }, Ct));

        Assert.Contains("before a Snapshot", ex.Message);
        Assert.Null(viewer.Registry.Find(sessionId));
    }

    [Fact]
    public async Task Actions_for_another_Session_are_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot();
        await connection.InvokeAsync("Snapshot", snapshot, Ct);

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Actions", new[] { NewAction(snapshot.SessionId, 1), NewAction(Guid.NewGuid(), 2) }, Ct));

        Assert.Contains("reports Session", ex.Message);
        Assert.Empty(viewer.Registry.Find(snapshot.SessionId)!.Actions); // The whole batch is refused.
    }

    [Fact]
    public async Task A_second_Session_on_one_connection_is_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var first = NewSnapshot();
        var second = NewSnapshot();
        await connection.InvokeAsync("Snapshot", first, Ct);

        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", second, Ct));

        Assert.Contains("open a new connection", ex.Message);
        Assert.Null(viewer.Registry.Find(second.SessionId));
    }

    public static TheoryData<string, Snapshot> InvalidSnapshots => new()
    {
        { "empty SessionId", NewSnapshot(Guid.Empty) },
        { "no Features list", NewSnapshot() with { Features = null! } },
        { "Feature 'Counter' more than once", NewSnapshot(features: [new("Counter", "A", "{}"), new("Counter", "A", "{}")]) },
    };

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public async Task An_invalid_Snapshot_is_rejected(string problem, Snapshot snapshot)
    {
        await using var connection = await viewer.ConnectAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Ct));

        Assert.Contains(problem, ex.Message);
        Assert.Equal(HubConnectionState.Connected, connection.State); // Rejections keep the connection open.
    }

    [Fact]
    public async Task Rejections_warn_once_per_connection_then_log_at_debug()
    {
        await using var connection = await viewer.ConnectAsync();
        var marker = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Actions", new[] { NewAction(marker, 1) }, Ct));

        bool OnThisConnection((LogLevel Level, string Message) l) => l.Message.Contains(connection.ConnectionId!);
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), OnThisConnection);
        Assert.Equal(2, viewer.IngestLogs(LogLevel.Debug).Count(OnThisConnection));
    }

    [Fact]
    public async Task Closing_the_connection_reaches_the_registry()
    {
        var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot();
        await connection.InvokeAsync("Snapshot", snapshot, Ct);

        await connection.DisposeAsync();

        await Wait.Until(() => viewer.Registry.Find(snapshot.SessionId)!.ConnectionId is null);
    }

    [Fact]
    public async Task A_Snapshot_larger_than_SignalRs_default_limit_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var bigState = $$"""{"Text":"{{new string('x', 1024 * 1024)}}"}""";
        var snapshot = NewSnapshot(features: [new FeatureState("Big", "App.BigState", bigState)]);

        await connection.InvokeAsync("Snapshot", snapshot, Ct);

        Assert.Equal(bigState, viewer.Registry.Find(snapshot.SessionId)!.Snapshots[0].Features[0].StateJson);
    }

    [Theory]
    [InlineData("http://localhost:5001", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:5173", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://192.168.1.20:5000", false)]
    [InlineData("ftp://localhost", false)]
    public async Task CORS_allows_loopback_origins_only(string origin, bool allowed)
    {
        using var client = new HttpClient { BaseAddress = viewer.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Options, $"{IngestHub.Path}/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        if (allowed)
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        else
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Maximum_message_size_is_32_MB() =>
        Assert.Equal(32 * 1024 * 1024, IngestLimits.MaximumMessageSize);

    /// <summary>Records holding lists compare by reference; compare Snapshots by content.</summary>
    private sealed class SnapshotComparer : IEqualityComparer<Snapshot>
    {
        public static readonly SnapshotComparer Instance = new();

        public bool Equals(Snapshot? x, Snapshot? y) =>
            x is not null && y is not null &&
            x with { Features = [] } == y with { Features = [] } &&
            x.Features.SequenceEqual(y.Features);

        public int GetHashCode(Snapshot obj) => obj.SessionId.GetHashCode();
    }
}
