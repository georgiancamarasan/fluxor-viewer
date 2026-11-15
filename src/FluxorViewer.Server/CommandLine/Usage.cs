using FluxorViewer.Server.Sessions;

namespace FluxorViewer.Server.CommandLine;

/// <summary>What <c>fluxor-viewer --help</c> prints: the command line and every option with its default.</summary>
internal static class Usage
{
    /// <summary>Whether the developer asked for help (<c>--help</c> or <c>-h</c>) instead of starting the Viewer.</summary>
    public static bool IsRequested(IEnumerable<string> args) => args.Any(arg => arg is "--help" or "-h");

    /// <summary>The help text.</summary>
    public static string Text =>
        $"""
        Usage: fluxor-viewer [--port N] [--history N] [--disconnected N]

        Starts the Fluxor Viewer on http://localhost, reachable from this machine only.

        Options:
          --port N          The port to listen on, from 1 to 65535. Default: {ViewerPort.Default.Value}.
          --history N       The most entries kept in each Session's History, at least 1. Default: {HistoryLimit.Default.Value}.
          --disconnected N  The most disconnected Sessions kept, at least 0. Default: {DisconnectedSessionLimit.Default.Value}.
          -h, --help        Show this help.
        """;
}
