using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Trading;

namespace TraderApp.Tests;

public class MakerQualityTests
{
    private static OrderBook Book(decimal bidSize = 10m, decimal askSize = 5m, DateTime? received = null) => new()
    {
        ReceivedAtUtc = received ?? DateTime.UtcNow,
        Bids = new() { new() { 100m, bidSize }, new() { 99m, 10m } },
        Asks = new() { new() { 101m, askSize }, new() { 102m, 5m } }
    };

    [Fact]
    public void Allows_small_passive_order_when_book_supports_direction()
    {
        var verdict = MakerQuality.Evaluate(Book(), true, 0.1m, new TradingConfig(), DateTime.UtcNow);
        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Fact]
    public void Rejects_order_that_would_dominate_touch_queue()
    {
        var verdict = MakerQuality.Evaluate(Book(), true, 3m, new TradingConfig(), DateTime.UtcNow);
        Assert.False(verdict.Allowed);
        Assert.Contains("touch queue", verdict.Reason);
    }

    [Fact]
    public void Rejects_book_that_leans_against_direction()
    {
        var verdict = MakerQuality.Evaluate(Book(bidSize: 2m, askSize: 10m), true, 0.1m,
            new TradingConfig(), DateTime.UtcNow);
        Assert.False(verdict.Allowed);
        Assert.Contains("imbalance", verdict.Reason);
    }

    [Fact]
    public void Rejects_stale_book()
    {
        var received = DateTime.UtcNow.AddSeconds(-3);
        var verdict = MakerQuality.Evaluate(Book(received: received), true, 0.1m,
            new TradingConfig { MakerMaxBookAgeMs = 500 }, DateTime.UtcNow);
        Assert.False(verdict.Allowed);
        Assert.Contains("book age", verdict.Reason);
    }

    [Theory]
    [InlineData("https://api.bybit.com", "wss://stream.bybit.com/v5/public/linear")]
    [InlineData("https://api-testnet.bybit.com", "wss://stream-testnet.bybit.com/v5/public/linear")]
    public void Public_book_stream_uri_matches_environment(string apiUrl, string expected)
    {
        Assert.Equal(expected, BybitPublicOrderBookStream.BuildPublicStreamUri(apiUrl, "linear").ToString().TrimEnd('/'));
    }
}
