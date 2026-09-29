using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Trading;

namespace TraderApp.Backtest;

public static class BacktestRunner
{
    public static async Task<int> RunAsync(BotConfig cfg, int days, bool sweep, CancellationToken ct, decimal feeOverridePct = 0m, int endDaysAgo = 0, bool signalQuality = false)
    {
        using var client = new WhiteBitClient(cfg.Api);

        // endDaysAgo shifts the window back, so a parameter chosen on one
        // period can be validated on another it never saw.
        var toUtc = DateTime.UtcNow.Date.AddDays(-endDaysAgo);
        var fromUtc = toUtc.AddDays(-days);

        Console.WriteLine();
        Console.WriteLine($"  Backtest  {fromUtc:yyyy-MM-dd} .. {toUtc:yyyy-MM-dd}  ({days} days)");
        Console.WriteLine($"  Markets   {string.Join(", ", cfg.Trading.Markets)}");
        Console.WriteLine($"  Entry {cfg.Trading.EntryInterval} / regime {cfg.Trading.RegimeInterval}");

        // Filters that need live-only data cannot be replayed from klines:
        // the synthetic book has no depth and there is no funding history.
        // Leaving them on would veto every bar and measure nothing.
        var liveOnly = new List<string>();
        if (cfg.Signals.MinBookDepthUsd > 0) { cfg.Signals.MinBookDepthUsd = 0; liveOnly.Add("MinBookDepthUsd"); }
        if (cfg.Signals.StandAsideEnabled) { cfg.Signals.StandAsideEnabled = false; liveOnly.Add("StandAside"); }
        if (cfg.Trading.PartialTakeAtR > 0) liveOnly.Add("PartialTake (not simulated)");
        if (cfg.Trading.EntryHoursUtcStart != cfg.Trading.EntryHoursUtcEnd) liveOnly.Add("EntryHours (not simulated)");
        if (liveOnly.Count > 0)
            Console.WriteLine($"  Live-only  {string.Join(", ", liveOnly)} — ignored in replay");
        Console.WriteLine();

        var rules = await client.GetMarketRulesAsync(ct);
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "history-cache");
        var loader = new HistoryLoader(client, cacheDir);

        var data = new Dictionary<string, (IReadOnlyList<Candle> entry, IReadOnlyList<Candle> regime)>();
        foreach (var market in cfg.Trading.Markets)
        {
            if (!rules.ContainsKey(market)) { Log.Warn($"{market}: not listed, skipped"); continue; }
            var e = await loader.LoadAsync(market, cfg.Trading.EntryInterval, fromUtc, toUtc, ct);
            var r = await loader.LoadAsync(market, cfg.Trading.RegimeInterval, fromUtc, toUtc, ct);
            if (e.Count > 100 && r.Count > cfg.Signals.EmaRegime + 5) data[market] = (e, r);
            else Log.Warn($"{market}: not enough history ({e.Count} entry / {r.Count} regime bars)");
        }

        if (data.Count == 0) { Console.WriteLine("No usable history."); return 1; }

        if (signalQuality)
        {
            SignalQuality.Run(cfg, data);
            return 0;
        }

        if (!sweep)
        {
            var result = new Backtester(cfg, feeOverridePct: feeOverridePct).Run(data, rules);
            Report(result, cfg);
            return 0;
        }

        await SweepAsync(cfg, data, rules);
        return 0;
    }

    private static void Report(BacktestResult r, BotConfig cfg)
    {
        Console.WriteLine();
        Console.WriteLine("  ── Result ──────────────────────────────────────────");
        if (r.Trades == 0)
        {
            Console.WriteLine("  No trades taken. The filters rejected every bar.");
            Console.WriteLine("  Loosen MinScore / MinConfirmations / MinAgreeingWeight, or widen the window.");
            return;
        }

        Console.WriteLine($"  Trades          {r.Trades}");
        Console.WriteLine($"  Win rate        {r.WinRate:F1}%  ({r.Wins}W / {r.Trades - r.Wins}L)");
        Console.WriteLine($"  Scratches       {r.ScratchRate:F1}%  ({r.Scratches} ended within a fee of zero)");
        Console.WriteLine($"  Net             {r.NetUsd,8:F2} USDT   (gross {r.GrossUsd:F2}, fees {r.FeesUsd:F2})");
        Console.WriteLine($"  Expectancy      {r.ExpectancyR,8:F3} R per trade GROSS (before fees)");
        Console.WriteLine($"  Net per trade   {r.NetUsd / r.Trades,8:F4} USDT after fees");
        Console.WriteLine($"  Profit factor   {r.ProfitFactor,8:F2}");
        Console.WriteLine($"  Max drawdown    {r.MaxDrawdownUsd,8:F2} USDT");
        Console.WriteLine($"  Avg win / loss  {r.AvgWinR,8:F2}R / {r.AvgLossR:F2}R");
        Console.WriteLine($"  Avg MFE / MAE   {r.AvgMfeR,8:F2}R / {r.AvgMaeR:F2}R");

        var feeShare = r.GrossUsd != 0 ? r.FeesUsd / Math.Abs(r.GrossUsd) * 100m : 0;
        Console.WriteLine($"  Fees are {feeShare:F0}% of gross PnL");

        Console.WriteLine("\n  Exits");
        foreach (var (reason, n) in r.ExitReasons.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {reason,-14} {n,4}  ({(decimal)n / r.Trades * 100m:F0}%)");

        Console.WriteLine("\n  Per market");
        foreach (var (m, v) in r.PerMarket.OrderByDescending(kv => kv.Value.usd))
            Console.WriteLine($"    {m,-12} {v.n,4} trades  {v.usd,8:F2} USDT");

        // The number that decides whether this is worth running at all.
        var breakEven = r.AvgLossR != 0
            ? -r.AvgLossR / (r.AvgWinR - r.AvgLossR) * 100m
            : 0m;
        Console.WriteLine($"\n  Win rate needed for this win/loss profile: {breakEven:F1}%");
        // The verdict must be based on NET, not on the gross R expectancy - a
        // strategy can have a real directional edge and still lose money.
        if (r.NetUsd > 0)
            Console.WriteLine("  Net positive over this window.");
        else if (r.GrossUsd > 0)
            Console.WriteLine($"  Gross edge is positive (+{r.GrossUsd:F2}) but fees ({r.FeesUsd:F2}) exceed it.\n" +
                              "  The signal works; the cost structure does not. Do not run this live as-is.");
        else
            Console.WriteLine("  NEGATIVE even before fees - the signal has no edge in this window.");
    }

    /// <summary>
    /// Sweeps the handful of parameters that actually change the trade
    /// population, to show how sensitive the result is. A configuration that
    /// only works at one exact setting is fitted to the window, not to the market.
    /// </summary>
    private static async Task SweepAsync(
        BotConfig cfg,
        Dictionary<string, (IReadOnlyList<Candle> entry, IReadOnlyList<Candle> regime)> data,
        IReadOnlyDictionary<string, MarketRules> rules)
    {
        Console.WriteLine("\n  ── Sensitivity sweep ───────────────────────────────");
        Console.WriteLine($"  {"minScore",9}{"conf",6}{"slATR",7}{"tpATR",7}{"trades",8}{"win%",7}{"expR",8}{"net$",9}");
        Console.WriteLine("  " + new string('-', 61));

        var scores = new[] { 0.55, 0.62, 0.68 };
        var confs = new[] { 2, 3, 4 };
        var sls = new[] { 1.2m, 1.5m, 1.8m };
        var tps = new[] { 2.0m, 3.0m, 4.0m };

        var rows = new List<(double s, int c, decimal sl, decimal tp, BacktestResult r)>();

        foreach (var s in scores)
        foreach (var c in confs)
        foreach (var sl in sls)
        foreach (var tp in tps)
        {
            var trial = Clone(cfg);
            trial.Signals.MinScore = s;
            trial.Signals.MinConfirmations = c;
            trial.Trading.StopLossAtrMult = sl;
            trial.Trading.TakeProfitAtrMult = tp;

            var res = new Backtester(trial).Run(data, rules);
            rows.Add((s, c, sl, tp, res));
        }

        foreach (var row in rows.OrderByDescending(x => x.r.NetUsd).Take(15))
            Console.WriteLine($"  {row.s,9:F2}{row.c,6}{row.sl,7:F1}{row.tp,7:F1}" +
                              $"{row.r.Trades,8}{row.r.WinRate,7:F1}{row.r.ExpectancyR,8:F3}{row.r.NetUsd,9:F2}");

        var profitable = rows.Count(x => x.r.NetUsd > 0 && x.r.Trades >= 10);
        var tested = rows.Count(x => x.r.Trades >= 10);
        Console.WriteLine($"\n  {profitable}/{tested} parameter sets with >=10 trades were profitable.");
        Console.WriteLine(profitable * 2 >= tested
            ? "  Broadly profitable across settings - the edge is not a single lucky cell."
            : "  Profitable only in a minority of settings - most likely curve-fitting, not edge.");

        await Task.CompletedTask;
    }

    private static BotConfig Clone(BotConfig c) => new()
    {
        Api = c.Api,
        Telegram = c.Telegram,
        Risk = c.Risk,
        Trading = new TradingConfig
        {
            Markets = c.Trading.Markets,
            EntryInterval = c.Trading.EntryInterval,
            RegimeInterval = c.Trading.RegimeInterval,
            CandleLookback = c.Trading.CandleLookback,
            StopLossAtrMult = c.Trading.StopLossAtrMult,
            TakeProfitAtrMult = c.Trading.TakeProfitAtrMult,
            HardStopMultiplier = c.Trading.HardStopMultiplier,
            BreakEvenAtR = c.Trading.BreakEvenAtR,
            BreakEvenBufferR = c.Trading.BreakEvenBufferR,
            TrailStartAtR = c.Trading.TrailStartAtR,
            TrailAtrMult = c.Trading.TrailAtrMult,
            GiveBackArmR = c.Trading.GiveBackArmR,
            GiveBackDropR = c.Trading.GiveBackDropR,
            MaxPositionMinutes = c.Trading.MaxPositionMinutes,
            ScratchUnprofitableMinutes = c.Trading.ScratchUnprofitableMinutes,
            ScratchMaxFavourableR = c.Trading.ScratchMaxFavourableR,
            ScratchOnlyAboveR = c.Trading.ScratchOnlyAboveR,
            PostOnlyEntries = c.Trading.PostOnlyEntries,
            EntryOrderTimeoutSeconds = c.Trading.EntryOrderTimeoutSeconds
        },
        Signals = new SignalConfig
        {
            EmaFast = c.Signals.EmaFast, EmaSlow = c.Signals.EmaSlow, EmaRegime = c.Signals.EmaRegime,
            RsiPeriod = c.Signals.RsiPeriod, AtrPeriod = c.Signals.AtrPeriod, AdxPeriod = c.Signals.AdxPeriod,
            BollingerPeriod = c.Signals.BollingerPeriod, BollingerStdDev = c.Signals.BollingerStdDev,
            VolumeMaPeriod = c.Signals.VolumeMaPeriod,
            MinScore = c.Signals.MinScore, MinConfirmations = c.Signals.MinConfirmations,
            MinTriggerWeight = c.Signals.MinTriggerWeight, MinAgreeingWeight = c.Signals.MinAgreeingWeight,
            MinVolumeRatio = c.Signals.MinVolumeRatio,
            MaxTrendRsiLong = c.Signals.MaxTrendRsiLong, MinTrendRsiShort = c.Signals.MinTrendRsiShort,
            RequireMacdAlign = c.Signals.RequireMacdAlign,
            RequireDecidedHtf = c.Signals.RequireDecidedHtf,
            BookVetoMinImbalance = c.Signals.BookVetoMinImbalance,
            PullbackTriggerEnabled = c.Signals.PullbackTriggerEnabled,
            PullbackMinVolumeRatio = c.Signals.PullbackMinVolumeRatio,
            StandAsideEnabled = false,
            AdxTrendThreshold = c.Signals.AdxTrendThreshold,
            RsiOversold = c.Signals.RsiOversold, RsiOverbought = c.Signals.RsiOverbought,
            RsiExtremeHigh = c.Signals.RsiExtremeHigh, RsiExtremeLow = c.Signals.RsiExtremeLow,
            OrderBookDepth = c.Signals.OrderBookDepth, ImbalanceThreshold = c.Signals.ImbalanceThreshold,
            MaxSpreadBps = c.Signals.MaxSpreadBps, MinAtrPct = c.Signals.MinAtrPct, MaxAtrPct = c.Signals.MaxAtrPct
        }
    };
}
