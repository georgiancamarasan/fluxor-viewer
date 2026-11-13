namespace FluxorViewer.Server.Tests;

/// <summary>
/// The system clock with a fixed local time zone, UTC+2, so the times a page shows don't depend on the machine running
/// the tests. Unlike <c>FakeTimeProvider</c>, time still passes, so the page's render throttle keeps working.
/// </summary>
internal sealed class FixedZoneTime : TimeProvider
{
    /// <summary>The local time zone: two hours ahead of UTC, all year.</summary>
    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
}
