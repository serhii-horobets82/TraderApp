using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Risk;

namespace TraderApp.Tests;

public class HardRiskRulesTests
{
    private static RiskManager Create(Action<RiskConfig>? tweak = null)
    {
        var risk = new RiskConfig();
        tweak?.Invoke(risk);
        return new RiskManager(risk, new TradingConfig());
    }

    [Fact]
    public void Session_drawdown_halts_at_configured_pct()
    {
        var rm = Create(c => c.MaxDailyDrawdownPct = 2m);
        rm.InitialiseEquity(10_000m);

        Assert.True(rm.CheckAccountHealth(9_800m, Array.Empty<Position>(), out var reason));
        Assert.True(rm.Halted);
        Assert.Contains("session drawdown", reason);
    }

    [Fact]
    public void Peak_drawdown_halts_even_when_still_above_start()
    {
        var rm = Create(c =>
        {
            c.MaxDailyDrawdownPct = 10m;
            c.MaxPeakDrawdownPct = 2m;
            c.MaxDailyProfitPct = 0m;
        });
        rm.InitialiseEquity(10_000m);

        Assert.False(rm.CheckAccountHealth(10_500m, Array.Empty<Position>(), out _));
        Assert.Equal(10_500m, rm.PeakEquity);

        Assert.True(rm.CheckAccountHealth(10_290m, Array.Empty<Position>(), out var reason));
        Assert.True(rm.Halted);
        Assert.Contains("peak drawdown", reason);
    }

    [Fact]
    public void Daily_profit_lock_halts_on_green_day()
    {
        var rm = Create(c =>
        {
            c.MaxDailyProfitPct = 1.5m;
            c.MaxPeakDrawdownPct = 0m;
        });
        rm.InitialiseEquity(10_000m);

        Assert.True(rm.CheckAccountHealth(10_150m, Array.Empty<Position>(), out var reason));
        Assert.True(rm.Halted);
        Assert.Contains("daily profit lock", reason);
    }

    [Fact]
    public void Consecutive_losses_hard_halt_when_enabled()
    {
        var rm = Create(c =>
        {
            c.MaxConsecutiveLosses = 3;
            c.HaltOnConsecutiveLosses = true;
        });

        rm.RecordTradeResult(false, -1m);
        rm.RecordTradeResult(false, -1m);
        Assert.False(rm.Halted);

        rm.RecordTradeResult(false, -1m);
        Assert.True(rm.Halted);
        Assert.Contains("consecutive losses", rm.HaltReason);
        Assert.False(rm.CanOpenNewPosition("BTC_USDT", Array.Empty<Position>(), out var reason));
        Assert.Contains("halted", reason);
    }

    [Fact]
    public void Fee_noise_does_not_count_toward_loss_streak()
    {
        var rm = Create(c =>
        {
            c.MaxConsecutiveLosses = 2;
            c.HaltOnConsecutiveLosses = true;
            c.FeeNoiseUsd = 0.12m;
        });

        rm.RecordTradeResult(false, -0.05m);
        rm.RecordTradeResult(false, -0.05m);
        Assert.False(rm.Halted);
        Assert.Equal(0, rm.ConsecutiveLosses);
    }

    [Fact]
    public void Max_trades_per_day_blocks_further_entries()
    {
        var rm = Create(c =>
        {
            c.MaxTradesPerDay = 2;
            c.MaxTradesPerHour = 10;
            c.PerMarketCooldownSeconds = 0;
            c.MaxConcurrentPositions = 5;
        });

        Assert.True(rm.CanOpenNewPosition("BTC_USDT", Array.Empty<Position>(), out _));
        rm.RecordEntry("BTC_USDT");
        rm.RecordEntry("ETH_USDT");

        Assert.False(rm.CanOpenNewPosition("SOL_USDT", Array.Empty<Position>(), out var reason));
        Assert.Contains("today", reason);
        Assert.Equal(2, rm.EntriesToday);
    }

    [Fact]
    public void Max_trades_per_hour_blocks_burst_entries()
    {
        var rm = Create(c =>
        {
            c.MaxTradesPerHour = 2;
            c.MaxTradesPerDay = 50;
            c.PerMarketCooldownSeconds = 0;
            c.MaxConcurrentPositions = 5;
        });

        rm.RecordEntry("BTC_USDT");
        rm.RecordEntry("ETH_USDT");

        Assert.False(rm.CanOpenNewPosition("SOL_USDT", Array.Empty<Position>(), out var reason));
        Assert.Contains("last hour", reason);
    }

    [Fact]
    public void Hard_defaults_validate()
    {
        var cfg = new BotConfig
        {
            Api = { Key = "k", Secret = "s" },
            Trading = { Markets = { "BTC_USDT" } },
        };
        // Bind-style defaults from RiskConfig class
        Assert.Empty(cfg.Validate());
        Assert.Equal(0.25m, cfg.Risk.RiskPerTradePct);
        Assert.Equal(1, cfg.Risk.MaxConcurrentPositions);
        Assert.True(cfg.Risk.HaltOnConsecutiveLosses);
        Assert.Equal(10, cfg.Risk.MaxTradesPerDay);
    }
}
