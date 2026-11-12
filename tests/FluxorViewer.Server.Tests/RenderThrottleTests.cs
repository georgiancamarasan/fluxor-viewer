using FluxorViewer.Server.Components;
using Microsoft.Extensions.Time.Testing;

namespace FluxorViewer.Server.Tests;

// UI-7: a burst of changes renders at most ~10 times a second, and always ends on a render.
public class RenderThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly FakeTimeProvider time = new();
    private readonly RenderThrottle throttle;
    private int renders;

    public RenderThrottleTests() => throttle = new RenderThrottle(time, Interval, () => renders++);

    [Fact]
    public void The_first_request_renders_at_once()
    {
        throttle.Request();

        Assert.Equal(1, renders);
    }

    [Fact]
    public void Requests_within_the_interval_become_one_render_at_its_end()
    {
        throttle.Request();
        time.Advance(TimeSpan.FromMilliseconds(10));
        throttle.Request();
        throttle.Request();
        Assert.Equal(1, renders);

        time.Advance(TimeSpan.FromMilliseconds(89));
        Assert.Equal(1, renders);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, renders);

        time.Advance(Interval * 5);
        Assert.Equal(2, renders);
    }

    [Fact]
    public void A_request_after_a_quiet_interval_renders_at_once()
    {
        throttle.Request();
        time.Advance(Interval);

        throttle.Request();

        Assert.Equal(2, renders);
    }

    [Fact]
    public void A_steady_stream_renders_once_per_interval()
    {
        for (var ms = 0; ms < 1000; ms++)
        {
            throttle.Request();
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.InRange(renders, 10, 11);
    }

    [Fact]
    public void Disposing_drops_a_pending_render()
    {
        throttle.Request();
        throttle.Request();

        throttle.Dispose();
        time.Advance(Interval);

        Assert.Equal(1, renders);
    }
}
