using TraderApp.Exchange;

namespace TraderApp.Tests;

/// <summary>
/// The closed-bar kline cache must actually hold data between bar closes.
/// The original formula expired entries at the newest closed bar's own close
/// time - always already in the past - so every 2s tick re-downloaded
/// history for every market and Bybit answered with retCode 10006 within
/// seconds of boot.
/// </summary>
public class KlineCacheTests
{
    private static readonly TimeSpan M15 = TimeSpan.FromMinutes(15);

    [Fact]
    public void Mid_bar_result_is_valid_until_the_forming_bar_closes()
    {
        // Newest closed bar opened 23:45, closed 00:00. Fetched at 00:07.
        var lastClosedOpen = new DateTime(2026, 9, 30, 23, 45, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 1, 0, 7, 0, DateTimeKind.Utc);

        var until = CandleInterval.ClosedBarValidUntil(lastClosedOpen, M15, now);

        Assert.Equal(new DateTime(2026, 10, 1, 0, 15, 0, DateTimeKind.Utc), until);
        Assert.True(until > now, "a fresh closed bar must be cacheable");
    }

    [Fact]
    public void Right_after_a_boundary_the_result_is_only_trusted_briefly()
    {
        // 2 s after the 00:00 close: the exchange may still be finalising it.
        var lastClosedOpen = new DateTime(2026, 9, 30, 23, 45, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 1, 0, 0, 2, DateTimeKind.Utc);

        var until = CandleInterval.ClosedBarValidUntil(lastClosedOpen, M15, now);

        Assert.Equal(now.AddSeconds(5), until);
    }

    [Fact]
    public void Stale_history_is_not_cached()
    {
        // Newest closed bar is two bars behind: whatever this is, do not hold it.
        var lastClosedOpen = new DateTime(2026, 9, 30, 23, 15, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 1, 0, 7, 0, DateTimeKind.Utc);

        var until = CandleInterval.ClosedBarValidUntil(lastClosedOpen, M15, now);

        Assert.True(until <= now);
    }

    [Fact]
    public void Unknown_interval_falls_back_to_a_short_ttl()
    {
        var now = new DateTime(2026, 10, 1, 0, 7, 0, DateTimeKind.Utc);
        var until = CandleInterval.ClosedBarValidUntil(now.AddMinutes(-30), TimeSpan.Zero, now);
        Assert.Equal(now.AddSeconds(5), until);
    }
}
