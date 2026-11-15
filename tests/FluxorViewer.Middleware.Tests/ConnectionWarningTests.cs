using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// PK-5: the first connection to a ServerUrl that can't be opened is logged at Warning, so a first run that shows nothing
// in the Viewer says why; later failures, and connections lost after opening, stay at Debug.
public class ConnectionWarningTests
{
    private static Harness Refused(string serverUrl, FakeViewer? viewer = null) =>
        new(viewer ?? new FakeViewer { ConnectFailure = new HttpRequestException("Connection refused") }, configure: v => v.ServerUrl = serverUrl);

    private static LogLevel[] LevelsLoggedAbout(Harness harness, string serverUrl) =>
        [.. harness.Logs.Entries.Where(e => e.Message.Contains(serverUrl)).Select(e => e.Level)];

    [Fact]
    public async Task The_first_connection_that_fails_to_open_is_a_warning_naming_the_ServerUrl_and_the_reason()
    {
        await using var harness = Refused("http://localhost:6101");

        await harness.Start();
        await Wait.Until(() => harness.Logs.Entries.Any(e => e.Level == LogLevel.Warning));

        var warning = Assert.Single(harness.Logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(
            "FluxorViewer: couldn't connect to the Viewer at http://localhost:6101 (Connection refused); retrying, and Actions are kept until it connects. " +
            "Is fluxor-viewer running, and does ServerUrl match the URL it printed?",
            warning.Message);
    }

    [Fact]
    public async Task Later_failed_connections_stay_at_debug()
    {
        await using var harness = Refused("http://localhost:6102");
        await harness.Start();

        await harness.AdvanceUntil(() => harness.Viewer.Connections.Count == 3);
        await Wait.Until(() => LevelsLoggedAbout(harness, "http://localhost:6102").Length == 3);

        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug], LevelsLoggedAbout(harness, "http://localhost:6102"));
    }

    [Fact]
    public async Task Another_Session_reporting_to_the_same_ServerUrl_does_not_warn_again()
    {
        await using var first = Refused("http://localhost:6103");
        await first.Start();
        await Wait.Until(() => LevelsLoggedAbout(first, "http://localhost:6103").Length == 1);

        await using var second = Refused("http://localhost:6103");
        await second.Start();
        await Wait.Until(() => LevelsLoggedAbout(second, "http://localhost:6103").Length == 1);

        Assert.Equal([LogLevel.Debug], LevelsLoggedAbout(second, "http://localhost:6103"));
    }

    [Fact]
    public async Task A_connection_lost_after_it_opened_stays_at_debug()
    {
        await using var harness = Refused("http://localhost:6104", new FakeViewer { SnapshotFailure = new HttpRequestException("Reset") });

        await harness.Start();
        await Wait.Until(() => LevelsLoggedAbout(harness, "http://localhost:6104").Length == 1);

        Assert.Equal([LogLevel.Debug], LevelsLoggedAbout(harness, "http://localhost:6104"));
    }
}
