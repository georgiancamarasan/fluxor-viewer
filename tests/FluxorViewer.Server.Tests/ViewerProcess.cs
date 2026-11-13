using System.Diagnostics;

namespace FluxorViewer.Server.Tests;

/// <summary>The real Viewer run as its own process, for command-line arguments that stop it before it listens.</summary>
internal static class ViewerProcess
{
    /// <summary>Runs the real Viewer as <c>fluxor-viewer</c> would, for arguments that stop it before it listens.</summary>
    public static async Task<(int ExitCode, string Error)> StartAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(Wait.CancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(Wait.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Wait.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The Viewer didn't stop. Output: {await output}");
        }
        return (process.ExitCode, await error);
    }
}
