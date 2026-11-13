using System.Net;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Ingest;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-2: the hub accepts Snapshots and Actions from middleware and enforces the protocol.
public class IngestHubTests(Viewer viewer) : IClassFixture<Viewer>
{
    [Fact]
    public async Task A_Snapshot_and_then_batches_of_Actions_reach_the_registry()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(Guid.NewGuid());

        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);
        var session = viewer.Registry.Find(snapshot.SessionId);
        Assert.NotNull(session);
        Assert.Equal("TestApp", session.AppName);
        Assert.Equal(Runtime.Server, session.Runtime);
        Assert.Equal(connection.ConnectionId, session.ConnectionId);

        await connection.InvokeAsync("Actions", new[] { Increment(snapshot.SessionId, 1), Increment(snapshot.SessionId, 2) }, Wait.CancellationToken);
        session = viewer.Registry.Find(snapshot.SessionId)!;
        Assert.Equal([1L, 2L], session.Actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":0}""", session.Actions.First().Before["Counter"].StateJson);

        await connection.InvokeAsync("Actions", new[] { Increment(snapshot.SessionId, 3) }, Wait.CancellationToken);
        session = viewer.Registry.Find(snapshot.SessionId)!;
        Assert.Equal([1L, 2L, 3L], session.Actions.Select(a => a.Sequence));
        Assert.Equal("""{"Count":3}""", session.Current["Counter"].StateJson);
    }

    [Fact]
    public async Task A_later_Snapshot_for_the_same_Session_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Wait.CancellationToken);

        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId) with { LastSequence = 40, DroppedCount = 12 }, Wait.CancellationToken);

        Assert.Equal(40, viewer.Registry.Find(sessionId)!.LastSequence);
    }

    [Fact]
    public async Task A_different_minor_version_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(Guid.NewGuid()) with { ProtocolVersion = ProtocolVersion.Current with { Minor = ProtocolVersion.Current.Minor + 7 } };

        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);

        Assert.NotNull(viewer.Registry.Find(snapshot.SessionId));
    }

    [Fact]
    public async Task An_incompatible_major_version_is_rejected_and_logged_once_per_Session()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(Guid.NewGuid()) with { ProtocolVersion = new ProtocolVersion(ProtocolVersion.Current.Major + 1, 0) };

        var first = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken));
        Assert.Contains("protocol version 2.0 is not compatible", first.Message);
        Assert.True(Rejection.IsRejection(first.Message)); // So the middleware stops rather than retrying.
        Assert.Null(viewer.Registry.Find(snapshot.SessionId));
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), l => l.Message.Contains(snapshot.SessionId.ToString()));

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken));
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), l => l.Message.Contains(snapshot.SessionId.ToString()));

        await using var retry = await viewer.ConnectAsync();
        await Assert.ThrowsAsync<HubException>(() => retry.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken));
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), l => l.Message.Contains(snapshot.SessionId.ToString()));
    }

    [Fact]
    public async Task Actions_before_a_Snapshot_are_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1) }, Wait.CancellationToken));

        Assert.Contains("before a Snapshot", exception.Message);
        Assert.True(Rejection.IsRejection(exception.Message)); // So the middleware stops rather than retrying.
        Assert.Null(viewer.Registry.Find(sessionId));
    }

    [Fact]
    public async Task Actions_for_another_Session_are_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(Guid.NewGuid());
        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);

        var exception = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Actions", new[] { Increment(snapshot.SessionId, 1), Increment(Guid.NewGuid(), 2) }, Wait.CancellationToken));

        Assert.Contains("reports Session", exception.Message);
        Assert.Empty(viewer.Registry.Find(snapshot.SessionId)!.Actions); // The whole batch is refused.
    }

    [Fact]
    public async Task A_second_Session_on_one_connection_is_rejected()
    {
        await using var connection = await viewer.ConnectAsync();
        var first = NewSnapshot(Guid.NewGuid());
        var second = NewSnapshot(Guid.NewGuid());
        await connection.InvokeAsync("Snapshot", first, Wait.CancellationToken);

        var exception = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", second, Wait.CancellationToken));

        Assert.Contains("open a new connection", exception.Message);
        Assert.Null(viewer.Registry.Find(second.SessionId));
    }

    public static TheoryData<string, Snapshot> InvalidSnapshots => new()
    {
        { "empty SessionId", NewSnapshot(Guid.Empty) },
        { "no Features list", NewSnapshot(Guid.NewGuid()) with { Features = null! } },
        { "Feature 'Counter' more than once", NewSnapshot(Guid.NewGuid()) with { Features = [new("Counter", "A", "{}"), new("Counter", "A", "{}")] } },
        { "no AppName", NewSnapshot(Guid.NewGuid()) with { AppName = null! } },
        { "a Feature that is null or missing its Name, StateType or StateJson", NewSnapshot(Guid.NewGuid()) with { Features = [null!] } },
        { "a Feature that is null or missing its Name, StateType or StateJson", NewSnapshot(Guid.NewGuid()) with { Features = [new(null!, "A", "{}")] } },
        { "a Feature that is null or missing its Name, StateType or StateJson", NewSnapshot(Guid.NewGuid()) with { Features = [new("Counter", null!, "{}")] } },
        { "a Feature that is null or missing its Name, StateType or StateJson", NewSnapshot(Guid.NewGuid()) with { Features = [new("Counter", "A", null!)] } },
    };

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public async Task An_invalid_Snapshot_is_rejected(string problem, Snapshot snapshot)
    {
        await using var connection = await viewer.ConnectAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken));

        Assert.Contains(problem, exception.Message);
        Assert.Equal(HubConnectionState.Connected, connection.State); // Rejections keep the connection open.
        Assert.Null(viewer.Registry.Find(snapshot.SessionId));
    }

    public static TheoryData<string, ActionRecorded> InvalidActions => new()
    {
        { "missing its ActionType, Payload or ChangedFeatures", Increment(Guid.Empty, 2) with { ActionType = null! } },
        { "missing its ActionType, Payload or ChangedFeatures", Increment(Guid.Empty, 2) with { Payload = null! } },
        { "missing its ActionType, Payload or ChangedFeatures", Increment(Guid.Empty, 2) with { ChangedFeatures = null! } },
        { "a Changed Feature that is null or missing its Name, StateType or StateJson", Increment(Guid.Empty, 2) with { ChangedFeatures = [null!] } },
        { "a Changed Feature that is null or missing its Name, StateType or StateJson", Increment(Guid.Empty, 2) with { ChangedFeatures = [new(null!, "A", "{}")] } },
    };

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public async Task An_Actions_batch_with_an_invalid_Action_is_refused_whole(string problem, ActionRecorded invalid)
    {
        await using var connection = await viewer.ConnectAsync();
        var sessionId = Guid.NewGuid();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Wait.CancellationToken);

        var exception = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Actions", new[] { Increment(sessionId, 1), invalid with { SessionId = sessionId } }, Wait.CancellationToken));

        Assert.Contains(problem, exception.Message);
        Assert.Empty(viewer.Registry.Find(sessionId)!.Actions);
    }

    [Fact]
    public async Task Rejections_warn_once_per_connection_then_log_at_debug()
    {
        await using var connection = await viewer.ConnectAsync();
        var marker = Guid.NewGuid();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Actions", new[] { Increment(marker, 1) }, Wait.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Actions", new[] { Increment(marker, 1) }, Wait.CancellationToken));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Actions", new[] { Increment(marker, 1) }, Wait.CancellationToken));

        bool OnThisConnection((LogLevel Level, string Message) l) => l.Message.Contains(connection.ConnectionId!);
        Assert.Single(viewer.IngestLogs(LogLevel.Warning), OnThisConnection);
        Assert.Equal(2, viewer.IngestLogs(LogLevel.Debug).Count(OnThisConnection));
    }

    [Fact]
    public async Task Closing_the_connection_reaches_the_registry()
    {
        var connection = await viewer.ConnectAsync();
        var snapshot = NewSnapshot(Guid.NewGuid());
        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);

        await connection.DisposeAsync();

        await Wait.Until(() => viewer.Registry.Find(snapshot.SessionId)!.ConnectionId is null);
    }

    [Fact]
    public async Task A_Snapshot_larger_than_SignalRs_default_limit_is_accepted()
    {
        await using var connection = await viewer.ConnectAsync();
        var bigState = $$"""{"Text":"{{new string('x', 1024 * 1024)}}"}""";
        var snapshot = NewSnapshot(Guid.NewGuid()) with { Features = [new FeatureState("Big", "App.BigState", bigState)] };

        await connection.InvokeAsync("Snapshot", snapshot, Wait.CancellationToken);

        Assert.Equal(bigState, viewer.Registry.Find(snapshot.SessionId)!.Current["Big"].StateJson);
    }

    /// <summary>The browser's CORS preflight for a Host app at <paramref name="origin"/> negotiating with the ingest hub.</summary>
    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var client = new HttpClient { BaseAddress = viewer.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Options, $"{IngestHub.Path}/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return await client.SendAsync(request, Wait.CancellationToken);
    }

    [Theory]
    [InlineData("http://localhost:5001")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:5173")]
    public async Task CORS_allows_loopback_origins(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("ftp://localhost")]
    public async Task CORS_refuses_other_origins(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Maximum_message_size_is_32_MB() =>
        Assert.Equal(32 * 1024 * 1024, JsonLimits.MaxMessageSize);
}
