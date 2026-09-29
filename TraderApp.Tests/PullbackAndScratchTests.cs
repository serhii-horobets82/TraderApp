using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Signals;
using TraderApp.Trading;

namespace TraderApp.Tests;

public class PullbackAndScratchTests
{
    private sealed class Trade : ITradeState
    {
        public bool IsLong { get; init; } = true;
        public decimal EntryPrice { get; init; } = 100m;
        public decimal EntryAtr { get; init; } = 1m;
        public DateTime OpenedUtc { get; init; } = DateTime.UtcNow.AddMinutes(-200);
        public decimal StopPrice { get; set; } = 98.5m;
        public decimal TakeProfitPrice { get; set; } = 103.5m;
        public decimal HighWater { get; set; }
        public bool MovedToBreakEven { get; set; }
        public decimal MaxFavourableR { get; set; } = 0.05m;
    }

    private static TradingConfig ScratchCfg(decimal onlyAbove) => new()
    {
        ScratchUnprofitableMinutes = 150,
        ScratchMaxFavourableR = 0.25m,
        ScratchOnlyAboveR = onlyAbove,
        MaxPositionMinutes = 0,
        GiveBackArmR = 0,
        StopLossAtrMult = 1.5m,
    };

    [Fact]
    public void Deep_trade_is_left_to_its_stop_not_scratched()
    {
        // mark 98.9 → -1.1 / 1.5 = -0.73R: below the -0.35 gate, stop is 0.27R away.
        var reason = ExitRules.Decide(new Trade(), 98.9m, DateTime.UtcNow, ScratchCfg(-0.35m), 0.2m, false, false);
        Assert.Null(reason);
    }

    [Fact]
    public void Near_flat_trade_is_scratched()
    {
        // mark 99.7 → -0.2R
        var reason = ExitRules.Decide(new Trade(), 99.7m, DateTime.UtcNow, ScratchCfg(-0.35m), 0.2m, false, false);
        Assert.NotNull(reason);
        Assert.StartsWith("never worked", reason);
    }

    [Fact]
    public void Gate_disabled_at_minus_one_restores_old_behaviour()
    {
        var reason = ExitRules.Decide(new Trade(), 98.9m, DateTime.UtcNow, ScratchCfg(-1m), 0.2m, false, false);
        Assert.NotNull(reason);
        Assert.StartsWith("never worked", reason);
    }

    [Fact]
    public void Pullback_is_a_trigger_component()
    {
        Assert.True(new SignalComponent("pullback", Direction.Long, 0.14, "").IsTrigger);
    }

    /// <summary>
    /// A steady 15m uptrend, a 5-bar dip to the slow EMA, then a bullish bar
    /// that closes back above it. The pullback component must vote long on
    /// the last closed bar; the RSI trigger cannot (no cross of 50 from below).
    /// </summary>
    [Fact]
    public void Pullback_to_slow_ema_in_uptrend_votes_long()
    {
        var cfg = new SignalConfig { PullbackTriggerEnabled = true, PullbackMinVolumeRatio = 1.0m };
        var engine = new SignalEngine(cfg);

        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new List<Candle>();
        decimal p = 100m;
        // 110 bars trending up ~0.12%/bar with a small wobble.
        for (int k = 0; k < 110; k++)
        {
            var open = p;
            var close = p * (1 + (k % 3 == 0 ? -0.0004m : 0.0016m));
            var high = Math.Max(open, close) * 1.0008m;
            var low = Math.Min(open, close) * 0.9992m;
            entry.Add(new Candle(t0.AddMinutes(15 * k), open, high, low, close, 1000m, 1000m * close));
            p = close;
        }
        // 5-bar pullback: -0.35%/bar, lows reaching the EMA21 zone.
        for (int k = 0; k < 5; k++)
        {
            var open = p;
            var close = p * 0.9965m;
            entry.Add(new Candle(t0.AddMinutes(15 * entry.Count), open, open * 1.0005m, close * 0.997m, close, 900m, 900m * close));
            p = close;
        }
        // Reclaim bar: tags below then closes strongly back up. This is the last CLOSED bar.
        {
            var open = p;
            var close = p * 1.009m;
            entry.Add(new Candle(t0.AddMinutes(15 * entry.Count), open, close * 1.0005m, open * 0.996m, close, 1400m, 1400m * close));
            p = close;
        }
        // Forming bar (ignored by the engine).
        entry.Add(new Candle(t0.AddMinutes(15 * entry.Count), p, p * 1.001m, p * 0.999m, p * 1.0005m, 100m, 100m * p));

        // 1h regime: steady uptrend so regime = Long.
        var regime = new List<Candle>();
        decimal rp = 90m;
        for (int k = 0; k < 80; k++)
        {
            var open = rp;
            var close = rp * 1.0025m;
            regime.Add(new Candle(t0.AddHours(k - 80), open, close * 1.001m, open * 0.999m, close, 4000m, 4000m * close));
            rp = close;
        }

        var mid = entry[^1].Close;
        var book = new OrderBook
        {
            Bids = new List<List<decimal>> { new() { mid * 0.9998m, 50m } },
            Asks = new List<List<decimal>> { new() { mid * 1.0002m, 50m } },
        };

        var s = engine.Evaluate("TEST_USDT", entry, regime, book);
        var pullback = s.Components.FirstOrDefault(c => c.Name == "pullback");

        Assert.NotNull(pullback);
        Assert.Equal(Direction.Long, pullback!.Direction);
        Assert.Contains("held", pullback.Detail);
    }
}
