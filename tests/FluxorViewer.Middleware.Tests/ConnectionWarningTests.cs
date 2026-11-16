using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

// PK-5: the first connection in the process that can't be opened is logged at Warning, so a first run that shows nothing
// in the Viewer says why; later failures, from any Session, and connections lost after opening, stay at Debug.
// Each harness is its own Host app process: the warning is shared by the Sessions in one container.
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
    public async Task Another_Session_in_the_same_Host_app_does_not_warn_again()
    {
        await using var harness = Refused("http://localhost:6103");
        await harness.Start();
        await Wait.Until(() => LevelsLoggedAbout(harness, "http://localhost:6103").Length == 1);

        await harness.StartAnotherSession();
        await Wait.Until(() => LevelsLoggedAbout(harness, "http://localhost:6103").Length == 2);

        Assert.Equal([LogLevel.Warning, LogLevel.Debug], LevelsLoggedAbout(harness, "http://localhost:6103"));
    }

    [Fact]
    public async Task Each_Host_app_process_warns_once_whatever_other_apps_did()
    {
        await using var earlier = Refused("http://localhost:6105");
        await earlier.Start();
        await Wait.Until(() => LevelsLoggedAbout(earlier, "http://localhost:6105").Length == 1);

        await using var later = Refused("http://localhost:6105");
        await later.Start();
        await Wait.Until(() => LevelsLoggedAbout(later, "http://localhost:6105").Length == 1);

        Assert.Equal([LogLevel.Warning], LevelsLoggedAbout(later, "http://localhost:6105"));
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
