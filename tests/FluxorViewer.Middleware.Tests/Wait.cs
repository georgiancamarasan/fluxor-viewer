namespace FluxorViewer.Middleware.Tests;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Polls until <paramref name="condition"/> holds; for things that happen on the background sender.</summary>
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10, Ct);
        }
    }
}
