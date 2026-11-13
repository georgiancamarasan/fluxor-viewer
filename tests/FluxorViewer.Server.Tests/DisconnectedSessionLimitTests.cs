using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

// IN-8: the Disconnected Session limit is set on the command line and applies to Sessions disconnecting from the real hub.
public class DisconnectedSessionLimitTests
{
    /// <summary>Reports a new Session to <paramref name="viewer"/>, then closes its connection and waits until it's gone or disconnected.</summary>
    private static async Task<Guid> ReportAndDisconnectAsync(Viewer viewer)
    {
        var sessionId = Guid.NewGuid();
        var connection = await viewer.ConnectAsync();
        await connection.InvokeAsync("Snapshot", NewSnapshot(sessionId), Wait.CancellationToken);
        await connection.DisposeAsync();
        await Wait.Until(() => viewer.Registry.Find(sessionId) is not { Status: SessionStatus.Connected });
        return sessionId;
    }

    private static async Task<Guid[]> ReportAndDisconnectAsync(Viewer viewer, int count)
    {
        List<Guid> sessionIds = [];
        for (var i = 0; i < count; i++)
            sessionIds.Add(await ReportAndDisconnectAsync(viewer));
        return [.. sessionIds];
    }

    private static Guid[] Kept(Viewer viewer) => [.. viewer.Registry.GetSessions().Select(s => s.SessionId)];

    [Fact]
    public async Task By_default_the_Viewer_keeps_10_disconnected_Sessions()
    {
        await using var viewer = new Viewer();
        var sessionIds = await ReportAndDisconnectAsync(viewer, 10);

        var eleventh = await ReportAndDisconnectAsync(viewer);

        Assert.Equal([.. sessionIds[1..], eleventh], Kept(viewer));
    }

    [Fact]
    public async Task The_disconnected_setting_sets_the_limit()
    {
        await using var viewer = new Viewer(new Dictionary<string, string?> { ["disconnected"] = "0" });

        await ReportAndDisconnectAsync(viewer);

        Assert.Empty(Kept(viewer));
    }

    [Theory]
    [InlineData("-1", "--disconnected must be a whole number of at least 0, not \"-1\".")]
    [InlineData("abc", "--disconnected must be a whole number of at least 0, not \"abc\".")]
    [InlineData("1.5", "--disconnected must be a whole number of at least 0, not \"1.5\".")]
    [InlineData("", "--disconnected must be a whole number of at least 0, not \"\".")]
    public async Task A_disconnected_value_that_is_not_a_whole_number_of_at_least_0_stops_the_Viewer_with_one_line(string value, string message)
    {
        var (exitCode, error) = await ViewerProcess.StartAsync("--disconnected", value);

        Assert.Equal(1, exitCode);
        Assert.Equal(message, error.Trim());
    }

    [Fact]
    public async Task A_disconnected_value_too_large_to_hold_says_so()
    {
        var (exitCode, error) = await ViewerProcess.StartAsync("--disconnected", "99999999999");

        Assert.Equal(1, exitCode);
        Assert.Equal("--disconnected can be at most 2147483647, not \"99999999999\".", error.Trim());
    }
}
