using TraderApp.Configuration;
using TraderApp.Exchange;

namespace TraderApp.Trading;

/// <summary>
/// Builds one BotConfig + client pair per active venue so risk, journals and
/// session files never collide when Both is selected.
/// </summary>
public static class VenueHost
{
    public static IReadOnlyList<(string Label, BotConfig Config, IExchangeClient Client)> Build(
        BotConfig root, ExchangeMode mode)
    {
        var list = new List<(string, BotConfig, IExchangeClient)>();

        if (mode is ExchangeMode.WhiteBIT or ExchangeMode.Both)
        {
            var cfg = CloneForVenue(root, ExchangeMode.WhiteBIT);
            list.Add(("WhiteBIT", cfg, new WhiteBitClient(cfg.Api)));
        }

        if (mode is ExchangeMode.Bybit or ExchangeMode.Both)
        {
            var cfg = CloneForVenue(root, ExchangeMode.Bybit);
            list.Add(("Bybit", cfg, new BybitClient(cfg.Bybit)));
        }

        return list;
    }

        public static BotConfig CloneForVenue(BotConfig root, ExchangeMode venue)
    {
        var trading = CloneTrading(root.Trading, venue, root.Bybit);
        var bybit = venue == ExchangeMode.Bybit
            ? CloneBybit(root.Bybit, trading.Markets)
            : root.Bybit;

        return new BotConfig
        {
            Api = root.Api,
            Bybit = bybit,
            Telegram = root.Telegram,
            Signals = CloneSignals(root.Signals, venue),
            Risk = CloneRisk(root.Risk, venue),
            Trading = trading
        };
    }

    private static BybitConfig CloneBybit(BybitConfig b, IReadOnlyList<string> markets) => new()
    {
        Key = b.Key,
        Secret = b.Secret,
        BaseUrl = b.BaseUrl,
        Category = b.Category,
        HttpTimeoutSeconds = b.HttpTimeoutSeconds,
        MaxRetries = b.MaxRetries,
        RecvWindowMs = b.RecvWindowMs,
        PostOnlyEntries = b.PostOnlyEntries,
        Markets = markets.ToList()
    };

    private static SignalConfig CloneSignals(SignalConfig s, ExchangeMode venue) => new()
    {
        EmaFast = s.EmaFast, EmaSlow = s.EmaSlow, EmaRegime = s.EmaRegime,
        RsiPeriod = s.RsiPeriod, AtrPeriod = s.AtrPeriod, AdxPeriod = s.AdxPeriod,
        BollingerPeriod = s.BollingerPeriod, BollingerStdDev = s.BollingerStdDev,
        VolumeMaPeriod = s.VolumeMaPeriod,
        MacdFast = s.MacdFast, MacdSlow = s.MacdSlow, MacdSignal = s.MacdSignal,
        StochPeriod = s.StochPeriod, StochSmoothK = s.StochSmoothK, StochSmoothD = s.StochSmoothD,
        StochOversold = s.StochOversold, StochOverbought = s.StochOverbought,
        DonchianPeriod = s.DonchianPeriod,
        MinScore = s.MinScore, MinConfirmations = s.MinConfirmations,
        MinTriggerWeight = s.MinTriggerWeight, MinAgreeingWeight = s.MinAgreeingWeight,
        MinDirectionEdge = s.MinDirectionEdge,
        MinVolumeRatio = s.MinVolumeRatio,
        MaxTrendRsiLong = s.MaxTrendRsiLong, MinTrendRsiShort = s.MinTrendRsiShort,
        RequireMacdAlign = s.RequireMacdAlign, RequireDecidedHtf = s.RequireDecidedHtf,
        BookVetoMinImbalance = s.BookVetoMinImbalance,
        StandAsideEnabled = s.StandAsideEnabled,
        AnchorMarket = venue == ExchangeMode.Bybit
            ? (string.IsNullOrWhiteSpace(s.AnchorMarket) || s.AnchorMarket.Contains('_', StringComparison.Ordinal)
                ? "BTCUSDT"
                : s.AnchorMarket)
            : s.AnchorMarket,
        AnchorInterval = s.AnchorInterval,
        MaxFundingRate = s.MaxFundingRate,
        AdxTrendThreshold = s.AdxTrendThreshold,
        RsiOversold = s.RsiOversold, RsiOverbought = s.RsiOverbought,
        RsiExtremeHigh = s.RsiExtremeHigh, RsiExtremeLow = s.RsiExtremeLow,
        OrderBookDepth = s.OrderBookDepth, ImbalanceThreshold = s.ImbalanceThreshold,
        MaxSpreadBps = s.MaxSpreadBps, MinAtrPct = s.MinAtrPct, MaxAtrPct = s.MaxAtrPct,
        MinBookDepthUsd = s.MinBookDepthUsd
    };

    private static RiskConfig CloneRisk(RiskConfig r, ExchangeMode venue)
    {
        // WhiteBIT keeps the configured path verbatim so existing Railway volumes
        // and halt files keep working. Bybit always gets a sibling *-bybit file.
        var path = venue == ExchangeMode.Bybit
            ? InsertSuffix(string.IsNullOrWhiteSpace(r.SessionStatePath)
                ? "data/session-state.json"
                : r.SessionStatePath, "bybit")
            : r.SessionStatePath;

        return new RiskConfig
        {
            RiskPerTradePct = r.RiskPerTradePct,
            MaxNotionalPerPositionUsd = r.MaxNotionalPerPositionUsd,
            MaxTotalNotionalUsd = r.MaxTotalNotionalUsd,
            MaxConcurrentPositions = r.MaxConcurrentPositions,
            MaxSameDirectionPositions = r.MaxSameDirectionPositions,
            OnePositionPerMarket = r.OnePositionPerMarket,
            MaxDailyDrawdownPct = r.MaxDailyDrawdownPct,
            MaxPeakDrawdownPct = r.MaxPeakDrawdownPct,
            MaxDailyProfitPct = r.MaxDailyProfitPct,
            ProfitLockRiskScale = r.ProfitLockRiskScale,
            WeekendRiskScale = r.WeekendRiskScale,
            ConvictionRiskFloor = r.ConvictionRiskFloor,
            MaxConsecutiveLosses = r.MaxConsecutiveLosses,
            HaltOnConsecutiveLosses = r.HaltOnConsecutiveLosses,
            LossCooldownMinutes = r.LossCooldownMinutes,
            MaxConsecutiveLossesPerMarket = r.MaxConsecutiveLossesPerMarket,
            MarketLossCooldownMinutes = r.MarketLossCooldownMinutes,
            PerMarketCooldownSeconds = r.PerMarketCooldownSeconds,
            MaxTradesPerHour = r.MaxTradesPerHour,
            MaxTradesPerDay = r.MaxTradesPerDay,
            RoundTripFeePct = r.RoundTripFeePct,
            MinNetEdgePct = r.MinNetEdgePct,
            MinStopPct = r.MinStopPct,
            MinStopFeeMultiple = r.MinStopFeeMultiple,
            FeeNoiseUsd = r.FeeNoiseUsd,
            FlattenOnMarginCall = r.FlattenOnMarginCall,
            ExcludeNonQuoteCollateralFromRiskEquity = r.ExcludeNonQuoteCollateralFromRiskEquity,
            CollateralValuationRefreshSeconds = r.CollateralValuationRefreshSeconds,
            PersistSessionState = r.PersistSessionState,
            SessionStatePath = path,
            ClearHaltOnBoot = r.ClearHaltOnBoot
        };
    }

    private static TradingConfig CloneTrading(TradingConfig t, ExchangeMode venue, BybitConfig bybit)
    {
        var markets = venue == ExchangeMode.Bybit
            ? (bybit.Markets.Count > 0 ? bybit.Markets.ToList() : DefaultBybitMarkets(t.Markets))
            : t.Markets.ToList();

        var journal = t.TradeJournalPath;
        if (venue == ExchangeMode.Bybit && !string.IsNullOrWhiteSpace(journal))
            journal = InsertSuffix(journal, "bybit");

        var positionState = t.PositionStatePath;
        if (venue == ExchangeMode.Bybit && !string.IsNullOrWhiteSpace(positionState))
            positionState = InsertSuffix(positionState, "bybit");

        return new TradingConfig
        {
            DryRun = t.DryRun,
            IUnderstandThisTradesRealMoney = t.IUnderstandThisTradesRealMoney,
            Markets = markets,
            ExchangeMode = t.ExchangeMode,
            Leverage = t.Leverage,
            EntryInterval = t.EntryInterval,
            RegimeInterval = t.RegimeInterval,
            CandleLookback = t.CandleLookback,
            LoopIntervalMs = t.LoopIntervalMs,
            StatusLogEveryTicks = t.StatusLogEveryTicks,
            StopLossAtrMult = t.StopLossAtrMult,
            TakeProfitAtrMult = t.TakeProfitAtrMult,
            HardStopMultiplier = t.HardStopMultiplier,
            BreakEvenAtR = t.BreakEvenAtR,
            BreakEvenEnabled = t.BreakEvenEnabled,
            BreakEvenBufferR = t.BreakEvenBufferR,
            TrailStartAtR = t.TrailStartAtR,
            TrailAtrMult = t.TrailAtrMult,
            GiveBackArmR = t.GiveBackArmR,
            GiveBackDropR = t.GiveBackDropR,
            MaxPositionMinutes = t.MaxPositionMinutes,
            ScratchUnprofitableMinutes = t.ScratchUnprofitableMinutes,
            ScratchMaxFavourableR = t.ScratchMaxFavourableR,
            MaxEntriesPerTick = t.MaxEntriesPerTick,
            // Bybit may override: its maker fee is well below taker, unlike
            // WhiteBIT where the two are equal and resting buys nothing.
            PostOnlyEntries = venue == ExchangeMode.Bybit && bybit.PostOnlyEntries.HasValue
                ? bybit.PostOnlyEntries.Value
                : t.PostOnlyEntries,
            EntryOrderTimeoutSeconds = t.EntryOrderTimeoutSeconds,
            PostOnlyFillWarningMinAttempts = t.PostOnlyFillWarningMinAttempts,
            PostOnlyFillWarningMinRatePct = t.PostOnlyFillWarningMinRatePct,
            PostOnlyFillWarningWindowMinutes = t.PostOnlyFillWarningWindowMinutes,
            PartialTakeAtR = t.PartialTakeAtR,
            PartialTakeFraction = t.PartialTakeFraction,
            EntryHoursUtcStart = t.EntryHoursUtcStart,
            EntryHoursUtcEnd = t.EntryHoursUtcEnd,
            TradeJournalPath = journal,
            PositionStatePath = positionState
        };
    }

    private static List<string> DefaultBybitMarkets(IEnumerable<string> whitebit)
    {
        var mapped = whitebit
            .Select(m => m.Replace("_", "", StringComparison.Ordinal))
            .Where(m => m.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return mapped.Count > 0
            ? mapped
            : new List<string> { "SOLUSDT", "AAVEUSDT", "XRPUSDT", "SUIUSDT" };
    }

    private static string InsertSuffix(string path, string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        if (name.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
            return path;
        // Splice in place rather than Path.Combine so the operator's separator
        // style ("data/x.json") survives unchanged on every OS.
        var stem = path[..^ext.Length];
        return $"{stem}-{suffix}{ext}";
    }
}
