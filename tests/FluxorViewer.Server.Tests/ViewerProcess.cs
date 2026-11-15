using System.Diagnostics;

namespace FluxorViewer.Server.Tests;

/// <summary>The real Viewer run as its own process, as <c>fluxor-viewer</c> runs it, for what its command line does.</summary>
internal sealed class ViewerProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Process process;

    private ViewerProcess(Process process, Uri url)
    {
        this.process = process;
        Url = url;
    }

    /// <summary>The URL the Viewer printed on start.</summary>
    public Uri Url { get; }

    /// <summary>Runs the Viewer with arguments that stop it before it listens (or never let it start), until it exits.</summary>
    /// <returns>Its exit code and everything it wrote to standard output and standard error.</returns>
    /// <exception cref="TimeoutException">The Viewer was still running after 30 seconds; it's killed.</exception>
    public static async Task<(int ExitCode, string Output, string Error)> StartAsync(params string[] args)
    {
        using var process = Start(args);
        var error = process.StandardError.ReadToEndAsync(Wait.CancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(Wait.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Wait.CancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The Viewer didn't stop. Output: {await output}");
        }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>Starts the Viewer and waits until it prints the URL it listens on; disposing it stops the Viewer.</summary>
    /// <exception cref="TimeoutException">No URL within 30 seconds, or the Viewer exited first; it's killed.</exception>
    public static async Task<ViewerProcess> ListenAsync(params string[] args)
    {
        var process = Start(args);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Wait.CancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var url = await PrintedUrlAsync(process, timeout.Token);
            DrainOutput(process);
            return new ViewerProcess(process, url);
        }
        catch (Exception exception) when (exception is OperationCanceledException or EndOfStreamException)
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw new TimeoutException("The Viewer never printed its URL.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        process.Dispose();
    }

    private static Process Start(string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    // Log lines name the URL too ("Now listening on: …"), so only the Viewer's own line counts.
    private static async Task<Uri> PrintedUrlAsync(Process process, CancellationToken cancellationToken)
    {
        const string prefix = "Fluxor Viewer: ";
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return new Uri(line[prefix.Length..]);
        }
        throw new EndOfStreamException("The Viewer exited without printing its URL.");
    }

    // Unread output would fill the pipe and block the Viewer once it logs enough.
    private static void DrainOutput(Process process)
    {
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
    }
}
