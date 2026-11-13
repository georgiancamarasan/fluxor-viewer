namespace FluxorViewer.Server.Components;

/// <summary>
/// Combines a burst of render requests into at most one render per interval: a request after a quiet interval renders
/// at once, and requests during the interval become one render at its end, so the last request is always rendered.
/// </summary>
public sealed class RenderThrottle(TimeProvider time, TimeSpan interval, Action render) : IDisposable
{
    private readonly Lock gate = new();
    private long lastRender = long.MinValue;
    private ITimer? pending;
    private bool disposed;

    /// <summary>Asks for a render. Safe to call from any thread; <c>render</c> may run on a timer thread.</summary>
    public void Request()
    {
        lock (gate)
        {
            if (disposed || pending is not null)
                return;
            var wait = interval - time.GetElapsedTime(lastRender);
            if (lastRender != long.MinValue && wait > TimeSpan.Zero)
            {
                pending = time.CreateTimer(_ => RenderPending(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }
            lastRender = time.GetTimestamp();
        }
        render();
    }

    private void RenderPending()
    {
        lock (gate)
        {
            if (disposed)
                return;
            pending?.Dispose();
            pending = null;
            lastRender = time.GetTimestamp();
        }
        render();
    }

    /// <summary>Cancels a pending render; no render happens after this returns.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Dispose();
            pending = null;
        }
    }
}
