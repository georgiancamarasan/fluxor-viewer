using System.Net;
using System.Net.Sockets;

namespace FluxorViewer.Server.Tests;

// PK-4: fluxor-viewer listens on localhost at --port (default 5299), prints its URL, and stops with one line when it can't.
public class CommandLineTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Something else listening on <paramref name="port"/> (0: any free port); null when something already was.</summary>
    private static TcpListener? Occupy(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            listener.Dispose();
            return null;
        }
    }

    [Fact]
    public async Task The_Viewer_prints_its_localhost_URL_on_start_and_serves_its_page_there()
    {
        var port = FreePort();

        await using var viewer = await ViewerProcess.ListenAsync("--port", port.ToString());

        Assert.Equal(new Uri($"http://localhost:{port}"), viewer.Url);
        using var client = new HttpClient { BaseAddress = viewer.Url };
        var response = await client.GetAsync("/", Wait.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_port_that_is_taken_stops_the_Viewer_with_one_line()
    {
        using var taken = Occupy(0)!;
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;

        var (exitCode, _, error) = await ViewerProcess.StartAsync("--port", port.ToString());

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"Port {port} is already in use. Start the Viewer on another port with --port N, and point the Host app's ServerUrl at it.",
            error.Trim());
    }

    [Fact]
    public async Task Without_a_port_the_Viewer_listens_on_5299()
    {
        using var taken = Occupy(5299);

        var (exitCode, _, error) = await ViewerProcess.StartAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "Port 5299 is already in use. Start the Viewer on another port with --port N, and point the Host app's ServerUrl at it.",
            error.Trim());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    [InlineData("")]
    public async Task A_port_outside_1_to_65535_stops_the_Viewer_with_one_line(string value)
    {
        var (exitCode, _, error) = await ViewerProcess.StartAsync("--port", value);

        Assert.Equal(1, exitCode);
        Assert.Equal($"--port must be a whole number from 1 to 65535, not \"{value}\".", error.Trim());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_documents_every_option_with_its_default_and_starts_nothing(string help)
    {
        var (exitCode, output, error) = await ViewerProcess.StartAsync(help);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(
            """
            Usage: fluxor-viewer [--port N] [--history N] [--disconnected N]

            Starts the Fluxor Viewer on http://localhost, reachable from this machine only.

            Options:
              --port N          The port to listen on, from 1 to 65535. Default: 5299.
              --history N       The most entries kept in each Session's History, at least 1. Default: 1000.
              --disconnected N  The most disconnected Sessions kept, at least 0. Default: 10.
              -h, --help        Show this help.
            """,
            output.Trim().ReplaceLineEndings("\n"));
    }
}
