using System.Runtime.CompilerServices;

namespace FluxorViewer.Tests;

/// <summary>Waiting on conditions that come true on another thread, such as the middleware's background sender.</summary>
internal static class Wait
{
    /// <summary>How long a wait gives up after.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>The running test's cancellation token.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    /// <exception cref="TimeoutException">It didn't hold within <see cref="Timeout"/>; the message quotes the condition.</exception>
    public static Task Until(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "") =>
        Until(condition, () => { }, description);

    /// <summary>Polls until <paramref name="condition"/> holds, running <paramref name="step"/> before each new check.</summary>
    /// <exception cref="TimeoutException">It didn't hold within <see cref="Timeout"/>; the message quotes the condition.</exception>
    public static async Task Until(Func<bool> condition, Action step, [CallerArgumentExpression(nameof(condition))] string description = "")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Waited {Timeout.TotalSeconds:0} s for {description}, which never held.");
            step();
            await Task.Delay(PollInterval, Ct);
        }
    }
}
