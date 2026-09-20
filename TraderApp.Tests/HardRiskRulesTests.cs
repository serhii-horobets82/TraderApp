using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Risk;
using TraderApp.Trading;

namespace TraderApp.Tests;

public class HardRiskRulesTests
{
    private static RiskManager Create(Action<RiskConfig>? tweak = null, SessionStateStore? store = null)
    {
        var risk = new RiskConfig { PersistSessionState = false };
        tweak?.Invoke(risk);
        return new RiskManager(risk, new TradingConfig(), store);
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
        Assert.Equal(16, cfg.Risk.MaxTradesPerDay);
    }

    [Fact]
    public void Halt_and_loss_streak_survive_restart_via_session_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");

        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxConsecutiveLosses = 3,
                HaltOnConsecutiveLosses = true,
            };
            var store = new SessionStateStore(cfg);
            var first = new RiskManager(cfg, new TradingConfig(), store);
            first.InitialiseEquity(200m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            Assert.True(first.Halted);
            Assert.True(File.Exists(path));

            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(201m);
            Assert.True(second.Halted);
            Assert.Contains("consecutive losses", second.HaltReason);
            Assert.Equal(3, second.ConsecutiveLosses);
            Assert.False(second.CanOpenNewPosition("SUI_USDT", Array.Empty<Position>(), out var reason));
            Assert.Contains("halted", reason);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ClearHaltOnBoot_discards_persisted_halt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");

        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxConsecutiveLosses = 2,
                HaltOnConsecutiveLosses = true,
            };
            var first = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            first.InitialiseEquity(200m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            Assert.True(first.Halted);

            cfg.ClearHaltOnBoot = true;
            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(200m);
            Assert.False(second.Halted);
            Assert.Equal(0, second.ConsecutiveLosses);
            Assert.True(second.CanOpenNewPosition("SUI_USDT", Array.Empty<Position>(), out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Daily_profit_lock_auto_clears_on_utc_day_roll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");

        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxDailyProfitPct = 1.5m,
                MaxPeakDrawdownPct = 0m,
                MaxDailyDrawdownPct = 50m,
            };
            var first = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            first.InitialiseEquity(200m);
            Assert.True(first.CheckAccountHealth(203.2m, Array.Empty<Position>(), out var reason));
            Assert.Contains("daily profit lock", reason);
            Assert.True(first.Halted);
            Assert.Equal(HaltKind.DailyProfit, first.Kind);
            Assert.True(first.HaltClearsOnUtcDay);

            // Pretend the lock was written yesterday so Restore sees a day roll.
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");
            var json = File.ReadAllText(path);
            json = System.Text.RegularExpressions.Regex.Replace(
                json, "\"entriesDay\"\\s*:\\s*\"[^\"]+\"", $"\"entriesDay\": \"{yesterday}\"");
            File.WriteAllText(path, json);

            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(203.2m);
            Assert.False(second.Halted);
            Assert.Equal(HaltKind.None, second.Kind);
            Assert.Equal(203.2m, second.SessionStartEquity);
            Assert.True(second.CanOpenNewPosition("SUI_USDT", Array.Empty<Position>(), out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Consecutive_loss_halt_survives_utc_day_roll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");

        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxConsecutiveLosses = 2,
                HaltOnConsecutiveLosses = true,
            };
            var first = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            first.InitialiseEquity(200m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            Assert.True(first.Halted);
            Assert.Equal(HaltKind.ConsecutiveLosses, first.Kind);
            Assert.False(first.HaltClearsOnUtcDay);

            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");
            var json = File.ReadAllText(path);
            json = System.Text.RegularExpressions.Regex.Replace(
                json, "\"entriesDay\"\\s*:\\s*\"[^\"]+\"", $"\"entriesDay\": \"{yesterday}\"");
            File.WriteAllText(path, json);

            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(200m);
            Assert.True(second.Halted);
            Assert.Equal(HaltKind.ConsecutiveLosses, second.Kind);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Consecutive_loss_halt_clears_on_restore_when_limit_raised()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");

        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxConsecutiveLosses = 3,
                HaltOnConsecutiveLosses = true,
            };
            var first = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            first.InitialiseEquity(200m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            first.RecordTradeResult(false, -1m);
            Assert.True(first.Halted);
            Assert.Equal(3, first.ConsecutiveLosses);

            cfg.MaxConsecutiveLosses = 10;
            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(200m);
            Assert.False(second.Halted);
            Assert.Equal(3, second.ConsecutiveLosses);
            Assert.True(second.CanOpenNewPosition("SUI_USDT", Array.Empty<Position>(), out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TickUtcDay_rebaselines_session_equity_when_not_halted()
    {
        var rm = Create(c =>
        {
            c.MaxDailyProfitPct = 1.5m;
            c.MaxPeakDrawdownPct = 0m;
        });
        rm.InitialiseEquity(200m);

        // Force a day roll by restoring from a crafted store via a second manager.
        var dir = Path.Combine(Path.GetTempPath(), "traderapp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session-state.json");
        try
        {
            var cfg = new RiskConfig
            {
                PersistSessionState = true,
                SessionStatePath = path,
                MaxDailyProfitPct = 1.5m,
                MaxPeakDrawdownPct = 0m,
                MaxDailyDrawdownPct = 50m,
            };
            var first = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            first.InitialiseEquity(200m);
            // Grow peak without locking.
            Assert.False(first.CheckAccountHealth(202m, Array.Empty<Position>(), out _));

            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");
            var json = File.ReadAllText(path);
            json = System.Text.RegularExpressions.Regex.Replace(
                json, "\"entriesDay\"\\s*:\\s*\"[^\"]+\"", $"\"entriesDay\": \"{yesterday}\"");
            File.WriteAllText(path, json);

            var second = new RiskManager(cfg, new TradingConfig(), new SessionStateStore(cfg));
            second.RestoreOrInitialise(210m);
            Assert.False(second.Halted);
            Assert.Equal(210m, second.SessionStartEquity);
            Assert.Equal(210m, second.PeakEquity);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class ExitRulesBreakEvenTests
{
    private sealed class Trade : ITradeState
    {
        public bool IsLong { get; init; } = true;
        public decimal EntryPrice { get; init; } = 100m;
        public decimal EntryAtr { get; init; } = 1m;
        public DateTime OpenedUtc { get; init; } = DateTime.UtcNow;
        public decimal StopPrice { get; set; } = 98.5m;
        public decimal TakeProfitPrice { get; set; } = 103.5m;
        public decimal HighWater { get; set; }
        public bool MovedToBreakEven { get; set; }
        public decimal MaxFavourableR { get; set; }
    }

    [Fact]
    public void BreakEvenEnabled_false_does_not_move_stop()
    {
        var t = new Trade();
        var cfg = new TradingConfig
        {
            BreakEvenEnabled = false,
            BreakEvenAtR = 0m,
            BreakEvenBufferR = 0.40m,
            StopLossAtrMult = 1.5m,
            TrailStartAtR = 0m,
        };

        // 1.0R of favourable move — would arm BE if enabled.
        var change = ExitRules.UpdateStops(t, 101.5m, cfg, roundTripFeePct: 0.2m, (p, _) => p);
        Assert.Null(change);
        Assert.False(t.MovedToBreakEven);
        Assert.Equal(98.5m, t.StopPrice);
    }

    [Fact]
    public void BreakEvenEnabled_true_moves_stop_once_trigger_cleared()
    {
        var t = new Trade();
        var cfg = new TradingConfig
        {
            BreakEvenEnabled = true,
            BreakEvenAtR = 0m,
            BreakEvenBufferR = 0.40m,
            StopLossAtrMult = 1.5m,
            TrailStartAtR = 0m,
        };

        var change = ExitRules.UpdateStops(t, 101.5m, cfg, roundTripFeePct: 0.2m, (p, _) => p);
        Assert.NotNull(change);
        Assert.True(t.MovedToBreakEven);
        Assert.True(t.StopPrice > t.EntryPrice);
    }

    [Fact]
    public void Scratch_exits_dead_trade_after_configured_minutes()
    {
        var t = new Trade
        {
            OpenedUtc = DateTime.UtcNow.AddMinutes(-50),
            MaxFavourableR = 0.10m,
            StopPrice = 98.5m,
        };
        var cfg = new TradingConfig
        {
            ScratchUnprofitableMinutes = 45,
            ScratchMaxFavourableR = 0.25m,
            MaxPositionMinutes = 0,
            GiveBackArmR = 0,
            StopLossAtrMult = 1.5m,
        };

        var reason = ExitRules.Decide(t, mark: 99.5m, DateTime.UtcNow, cfg, 0.2m,
            inTrouble: false, regimeAgainstUnderwater: false);
        Assert.NotNull(reason);
        Assert.StartsWith("never worked", reason);
    }
}
