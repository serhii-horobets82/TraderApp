using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Risk;
using TraderApp.Signals;
using TraderApp.Trading;

namespace TraderApp.Tests;

/// <summary>Profit-lock scaling, weekend scaling, entry hours, book depth, journal digest.</summary>
public class SoftRiskRulesTests
{
    private static RiskManager Create(Action<RiskConfig>? risk = null, Action<TradingConfig>? trading = null)
    {
        var r = new RiskConfig { PersistSessionState = false, MaxPeakDrawdownPct = 0m, MaxDailyDrawdownPct = 50m };
        risk?.Invoke(r);
        var t = new TradingConfig();
        trading?.Invoke(t);
        return new RiskManager(r, t);
    }

    private static MarketRules Rules() => new(new MarketInfo
    {
        Name = "SOL_USDT", Stock = "SOL", Money = "USDT",
        StockPrec = 3, MoneyPrec = 2, MakerFee = 0.1m, TakerFee = 0.1m,
        MinAmount = 0.01m, MinTotal = 5m, MaxTotal = 0m,
        IsCollateral = true, TradesEnabled = true, TickSize = "0.01", StepSize = "0.001"
    });

    [Fact]
    public void Profit_lock_with_scale_keeps_trading_at_reduced_size()
    {
        var rm = Create(c => { c.MaxDailyProfitPct = 1.5m; c.ProfitLockRiskScale = 0.5m; c.WeekendRiskScale = 1m; });
        rm.InitialiseEquity(10_000m);

        var full = rm.Size(true, 100m, 2m, 10_000m, 0m, Rules());
        Assert.True(full.Allowed);

        Assert.False(rm.CheckAccountHealth(10_160m, Array.Empty<Position>(), out _));
        Assert.False(rm.Halted);
        Assert.True(rm.ProfitLocked);
        Assert.Equal(0.5m, rm.RiskScale(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)));

        var half = rm.Size(true, 100m, 2m, 10_000m, 0m, Rules());
        Assert.True(half.Allowed);
        Assert.True(half.Amount < full.Amount);
        Assert.True(half.Amount <= full.Amount / 2m + Rules().StepSize);
    }

    [Fact]
    public void Profit_lock_with_zero_scale_still_halts()
    {
        var rm = Create(c => { c.MaxDailyProfitPct = 1.5m; c.ProfitLockRiskScale = 0m; });
        rm.InitialiseEquity(10_000m);

        Assert.True(rm.CheckAccountHealth(10_160m, Array.Empty<Position>(), out var reason));
        Assert.True(rm.Halted);
        Assert.Contains("daily profit lock", reason);
    }

    [Fact]
    public void Profit_lock_stays_on_when_equity_dips_back_below_cap()
    {
        var rm = Create(c => { c.MaxDailyProfitPct = 1.5m; c.ProfitLockRiskScale = 0.5m; });
        rm.InitialiseEquity(10_000m);
        Assert.False(rm.CheckAccountHealth(10_160m, Array.Empty<Position>(), out _));
        Assert.True(rm.ProfitLocked);

        Assert.False(rm.CheckAccountHealth(10_100m, Array.Empty<Position>(), out _));
        Assert.True(rm.ProfitLocked);
    }

    [Fact]
    public void Weekend_scale_applies_only_on_saturday_and_sunday()
    {
        var rm = Create(c => c.WeekendRiskScale = 0.5m);
        var saturday = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var monday = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);
        Assert.Equal(0.5m, rm.RiskScale(saturday));
        Assert.Equal(1m, rm.RiskScale(monday));
    }

    [Theory]
    [InlineData(6, 22, 5, false)]
    [InlineData(6, 22, 6, true)]
    [InlineData(6, 22, 21, true)]
    [InlineData(6, 22, 22, false)]
    [InlineData(22, 6, 23, true)]   // overnight window wraps midnight
    [InlineData(22, 6, 3, true)]
    [InlineData(22, 6, 12, false)]
    [InlineData(0, 0, 12, true)]    // equal = disabled
    public void Entry_hours_window(int start, int end, int hour, bool expected)
    {
        var t = new TradingConfig { EntryHoursUtcStart = start, EntryHoursUtcEnd = end };
        var now = new DateTime(2026, 9, 16, hour, 30, 0, DateTimeKind.Utc);
        Assert.Equal(expected, RiskManager.InsideEntryHours(t, now));
    }

    [Fact]
    public void Entry_hours_gate_blocks_new_positions_outside_window()
    {
        var h = DateTime.UtcNow.Hour;
        // A one-hour window that excludes the current hour.
        var start = (h + 2) % 24;
        var end = (h + 3) % 24;
        var rm = Create(trading: t => { t.EntryHoursUtcStart = start; t.EntryHoursUtcEnd = end; });
        Assert.False(rm.CanOpenNewPosition("SOL_USDT", Array.Empty<Position>(), out var reason));
        Assert.Contains("outside entry hours", reason);
    }

    [Fact]
    public void Thin_side_depth_is_min_of_bid_and_ask_notional()
    {
        var book = new OrderBook
        {
            Bids = new List<List<decimal>> { new() { 100m, 5m }, new() { 99m, 5m } },   // 995
            Asks = new List<List<decimal>> { new() { 101m, 20m }, new() { 102m, 20m } }, // 4060
        };
        Assert.Equal(995m, book.ThinSideDepthQuote(10));
        Assert.Equal(500m, book.ThinSideDepthQuote(1));
    }

    [Fact]
    public void Depth_veto_is_microstructure_so_it_can_clear_mid_bar()
    {
        var s = new TradeSignal
        {
            Market = "SOL_USDT", Direction = Direction.Long, Score = 0.7, Price = 100m, Atr = 1m,
            AtrPct = 1m, SpreadBps = 2m, Components = Array.Empty<SignalComponent>(),
            Vetoes = new[] { "depth 420 USDT on the thin side < 1000 (top 10 levels)" }
        };
        Assert.True(s.OnlyMicrostructureVetoes);
    }

    [Fact]
    public void Break_even_price_adds_fee_cushion_on_the_right_side()
    {
        var longTrade = new Probe { IsLong = true, EntryPrice = 100m, EntryAtr = 1m };
        var shortTrade = new Probe { IsLong = false, EntryPrice = 100m, EntryAtr = 1m };
        Assert.Equal(100.2m, ExitRules.BreakEvenPrice(longTrade, 0.2m, (p, _) => p));
        Assert.Equal(99.8m, ExitRules.BreakEvenPrice(shortTrade, 0.2m, (p, _) => p));
    }

    [Fact]
    public void Crossing_spread_is_charged_to_the_entry_edge_gate()
    {
        var rm = Create(r => { r.MinNetEdgePct = 0.10m; r.MinStopPct = 0.01m; r.MinStopFeeMultiple = 0m; },
            t => { t.StopLossAtrMult = 1m; t.TakeProfitAtrMult = 1m; });
        var rules = Rules(); // 0.2% round-trip fee

        // Target = 0.35%. It clears fees + 0.10% edge (0.30%), but must fail
        // after a 0.10% spread is included (required = 0.40%).
        var result = rm.Size(true, 100m, 0.35m, 10_000m, 0m, rules, entrySpreadPct: 0.10m);

        Assert.False(result.Allowed);
        Assert.Contains("entry spread 0.100%", result.Reason);
    }

    [Fact]
    public void Journal_round_trips_and_digest_reports_fee_share()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trades-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new TradeJournal(path);
            var day = new DateOnly(2026, 9, 19);
            journal.Append(new TradeRecord
            {
                PositionId = 1, Market = "SOL_USDT", Side = "long",
                OpenedUtc = day.ToDateTime(new TimeOnly(10, 0)), ClosedUtc = day.ToDateTime(new TimeOnly(11, 0)),
                HeldMinutes = 60, NetPnl = 1.20m, GrossPnl = 1.20m, Fees = 0.08m, ExitReason = "target",
                Entry = new EntryContext(0.66, new[] { "rsi", "bollinger" }, 0.5m, 3m, "up", 0.0001m, "up", 10, "Saturday", 1m)
            });
            journal.Append(new TradeRecord
            {
                PositionId = 2, Market = "AAVE_USDT", Side = "short",
                OpenedUtc = day.ToDateTime(new TimeOnly(14, 0)), ClosedUtc = day.ToDateTime(new TimeOnly(14, 40)),
                HeldMinutes = 40, NetPnl = -0.50m, GrossPnl = -0.50m, Fees = 0.08m, ExitReason = "stop"
            });
            journal.Append(new TradeRecord
            {
                PositionId = 3, Market = "SOL_USDT", Side = "long",
                OpenedUtc = day.AddDays(1).ToDateTime(new TimeOnly(1, 0)), ClosedUtc = day.AddDays(1).ToDateTime(new TimeOnly(2, 0)),
                NetPnl = 5m, GrossPnl = 5m, Fees = 0.1m, ExitReason = "target"
            });

            var trades = journal.ReadDay(day);
            Assert.Equal(2, trades.Count);
            Assert.Equal(new[] { "rsi", "bollinger" }, trades[0].Entry!.Components);

            var digest = TradeJournal.FormatDigest(day, trades, 215.5m);
            Assert.NotNull(digest);
            Assert.Contains("net +0.70", digest);
            Assert.Contains("1W/1L", digest);
            Assert.Contains("fees 0.16", digest);
            Assert.Contains("of gross", digest);
            Assert.Contains("target 1", digest);
            Assert.Contains("stop 1", digest);
            Assert.Contains("Balance 215.50", digest);

            Assert.Null(TradeJournal.FormatDigest(day.AddDays(5), journal.ReadDay(day.AddDays(5)), null));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class Probe : ITradeState
    {
        public bool IsLong { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal EntryAtr { get; init; }
        public DateTime OpenedUtc { get; init; } = DateTime.UtcNow;
        public decimal StopPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal HighWater { get; set; }
        public bool MovedToBreakEven { get; set; }
        public decimal MaxFavourableR { get; set; }
    }
}
