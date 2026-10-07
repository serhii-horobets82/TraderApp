using TraderApp.Notifications;

namespace TraderApp.Configuration;

/// <summary>
/// Which venues the process should run. Default WhiteBIT keeps existing
/// deploys unchanged: Api.* / WHITEBIT_* credentials and Trading.Markets.
/// </summary>
public enum ExchangeMode
{
    WhiteBIT = 0,
    Bybit = 1,
    Both = 2
}

/// <summary>
/// Root configuration. Bound from appsettings.json, then overridden by
/// environment variables (prefix TRADER_, e.g. TRADER_Api__Key).
/// Secrets must never live in appsettings.json - see README.
/// </summary>
public sealed class BotConfig
{
    public ApiConfig Api { get; set; } = new();
    public BybitConfig Bybit { get; set; } = new();
    public TradingConfig Trading { get; set; } = new();
    public SignalConfig Signals { get; set; } = new();
    public RiskConfig Risk { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();

    public ExchangeMode ResolveExchangeMode()
    {
        if (string.IsNullOrWhiteSpace(Trading.ExchangeMode)) return ExchangeMode.WhiteBIT;
        return Trading.ExchangeMode.Trim().ToLowerInvariant() switch
        {
            "whitebit" or "wb" or "white-bit" => ExchangeMode.WhiteBIT,
            "bybit" or "bb" => ExchangeMode.Bybit,
            "both" or "all" or "parallel" => ExchangeMode.Both,
            _ => throw new ArgumentException(
                $"Trading.ExchangeMode \"{Trading.ExchangeMode}\" is invalid. Use WhiteBIT, Bybit, or Both.")
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        ExchangeMode mode;
        try { mode = ResolveExchangeMode(); }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
            return errors;
        }

        var needWhiteBit = mode is ExchangeMode.WhiteBIT or ExchangeMode.Both;
        var needBybit = mode is ExchangeMode.Bybit or ExchangeMode.Both;

        if (needWhiteBit)
        {
            if (string.IsNullOrWhiteSpace(Api.Key))
                errors.Add("Api.Key is empty. Set TRADER_Api__Key or WHITEBIT_API_KEY.");
            if (string.IsNullOrWhiteSpace(Api.Secret))
                errors.Add("Api.Secret is empty. Set TRADER_Api__Secret or WHITEBIT_API_SECRET.");
            if (Trading.Markets.Count == 0)
                errors.Add("Trading.Markets is empty.");
        }

        if (needBybit)
        {
            if (string.IsNullOrWhiteSpace(Bybit.Key))
                errors.Add("Bybit.Key is empty. Set TRADER_Bybit__Key or BYBIT_API_KEY.");
            if (string.IsNullOrWhiteSpace(Bybit.Secret))
                errors.Add("Bybit.Secret is empty. Set TRADER_Bybit__Secret or BYBIT_API_SECRET.");
            if (Bybit.Markets.Count == 0 && Trading.Markets.Count == 0)
                errors.Add("Bybit.Markets is empty (and Trading.Markets has nothing to map).");
        }

        if (Trading.Leverage is < 1 or > 20)
            errors.Add("Trading.Leverage must be between 1 and 20.");
        if (Risk.RiskPerTradePct is <= 0 or > 2m)
            errors.Add("Risk.RiskPerTradePct must be in (0, 2]. More than 2% of equity per trade is not safe scalping.");
        if (Risk.TargetRiskPerTradePct is <= 0 or > 2m)
            errors.Add("Risk.TargetRiskPerTradePct must be in (0, 2].");
        if (Risk.RiskPerTradePct > Risk.TargetRiskPerTradePct)
            errors.Add("Risk.RiskPerTradePct cannot exceed Risk.TargetRiskPerTradePct.");
        if (Risk.MaxDailyDrawdownPct is < 0 or > 20m)
            errors.Add("Risk.MaxDailyDrawdownPct must be in [0, 20]. 0 disables the session drawdown cutout.");
        if (Risk.MaxPeakDrawdownPct is < 0 or > 20m)
            errors.Add("Risk.MaxPeakDrawdownPct must be in [0, 20]. 0 disables the peak cutout.");
        if (Risk.MaxDailyProfitPct is < 0 or > 50m)
            errors.Add("Risk.MaxDailyProfitPct must be in [0, 50]. 0 disables the profit lock.");
        if (Risk.MaxConcurrentPositions is < 1 or > 10)
            errors.Add("Risk.MaxConcurrentPositions must be between 1 and 10.");
        if (Risk.MaxTradesPerHour is < 1 or > 60)
            errors.Add("Risk.MaxTradesPerHour must be between 1 and 60.");
        if (Risk.MaxTradesPerDay is < 1 or > 200)
            errors.Add("Risk.MaxTradesPerDay must be between 1 and 200.");
        if (Risk.MaxConsecutiveLosses is < 1 or > 20)
            errors.Add("Risk.MaxConsecutiveLosses must be between 1 and 20.");
        if (Risk.MaxConsecutiveLossesPerMarket is < 0 or > 20)
            errors.Add("Risk.MaxConsecutiveLossesPerMarket must be in [0, 20]. 0 disables the per-market brake.");
        if (Risk.MarketLossCooldownMinutes is < 1 or > 1440)
            errors.Add("Risk.MarketLossCooldownMinutes must be between 1 and 1440.");
        if (Signals.MinScore is < 0.3 or > 1.0)
            errors.Add("Signals.MinScore must be in [0.3, 1.0].");
        if (Signals.MinDirectionEdge is < 0 or > 1.0)
            errors.Add("Signals.MinDirectionEdge must be in [0, 1.0]. 0 disables the uncertainty gate.");
        if (Risk.ConvictionRiskFloor is < 0 or > 1)
            errors.Add("Risk.ConvictionRiskFloor must be in [0, 1]. 1 disables conviction sizing.");
        if (Trading.TakeProfitAtrMult <= Trading.StopLossAtrMult * 0.8m)
            errors.Add("Trading.TakeProfitAtrMult should be at least 0.8x StopLossAtrMult, otherwise the win rate required to break even is unrealistic.");
        if (Trading.PartialTakeAtR < 0 || (Trading.PartialTakeAtR > 0 && Trading.PartialTakeFraction is <= 0 or >= 1))
            errors.Add("Trading.PartialTakeFraction must be in (0, 1) when PartialTakeAtR is set.");
        if (Trading.EntryHoursUtcStart is < 0 or > 23 || Trading.EntryHoursUtcEnd is < 0 or > 24)
            errors.Add("Trading.EntryHoursUtcStart must be 0-23 and EntryHoursUtcEnd 0-24.");
        if (Trading.PostOnlyFillWarningMinAttempts is < 0 or > 100)
            errors.Add("Trading.PostOnlyFillWarningMinAttempts must be in [0, 100]. 0 disables the alert.");
        if (Trading.PostOnlyFillWarningMinRatePct is < 0 or > 100)
            errors.Add("Trading.PostOnlyFillWarningMinRatePct must be in [0, 100].");
        if (Trading.PostOnlyFillWarningWindowMinutes is < 1 or > 1440)
            errors.Add("Trading.PostOnlyFillWarningWindowMinutes must be between 1 and 1440.");
        if (Bybit.PublicOrderBookFreshnessMs is < 250 or > 30_000)
            errors.Add("Bybit.PublicOrderBookFreshnessMs must be between 250 and 30000.");
        if (Trading.MakerMinDirectionalImbalance is < 0 or > 1)
            errors.Add("Trading.MakerMinDirectionalImbalance must be in [0, 1].");
        if (Trading.MakerMinTopLevelDepthUsd < 0)
            errors.Add("Trading.MakerMinTopLevelDepthUsd must be non-negative.");
        if (Trading.MakerMaxQueueFraction is <= 0 or > 1)
            errors.Add("Trading.MakerMaxQueueFraction must be in (0, 1].");
        if (Trading.MakerMaxBookAgeMs is < 100 or > 30_000)
            errors.Add("Trading.MakerMaxBookAgeMs must be between 100 and 30000.");
        if (Trading.MakerMaxSignalAgeMs is < 100 or > 60_000)
            errors.Add("Trading.MakerMaxSignalAgeMs must be between 100 and 60000.");
        if (Risk.ProfitLockRiskScale is < 0 or > 1)
            errors.Add("Risk.ProfitLockRiskScale must be in [0, 1]. 0 halts on the profit lock.");
        if (Risk.CollateralValuationRefreshSeconds is < 5 or > 300)
            errors.Add("Risk.CollateralValuationRefreshSeconds must be between 5 and 300.");
        if (Risk.WeekendRiskScale is <= 0 or > 1)
            errors.Add("Risk.WeekendRiskScale must be in (0, 1].");
        if (Telegram.Enabled && string.IsNullOrWhiteSpace(Telegram.BotToken))
            errors.Add("Telegram.Enabled is true but Telegram.BotToken is empty. Set TRADER_Telegram__BotToken or TELEGRAM_BOT_TOKEN.");
        if (Telegram.Enabled && !string.IsNullOrWhiteSpace(Telegram.ChatId)
                             && !TelegramNotifier.LooksLikeChatId(Telegram.ChatId))
            errors.Add($"Telegram.ChatId is not a valid chat id: \"{Telegram.ChatId}\". It must be a number " +
                       "(e.g. 123456789 or -1001234567890) or an @channelusername. Check appsettings.json - " +
                       "its value is used unless empty, in which case TELEGRAM_CHAT_ID is read instead.");

        return errors;
    }
}

public sealed class ApiConfig
{
    public string Key { get; set; } = "";
    public string Secret { get; set; } = "";

    /// <summary>https://whitebit.com or https://whitebit.eu</summary>
    public string BaseUrl { get; set; } = "https://whitebit.com";

    public int HttpTimeoutSeconds { get; set; } = 15;

    /// <summary>Minimum gap between two private (signed) requests, in ms. Keeps us well below the published limits.</summary>
    public int MinPrivateRequestGapMs { get; set; } = 60;

    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// Bybit USDT linear perps. Opt-in: leave Key/Secret empty and ExchangeMode at
/// WhiteBIT (default) and nothing Bybit-related is touched.
/// </summary>
public sealed class BybitConfig
{
    public string Key { get; set; } = "";
    public string Secret { get; set; } = "";

    /// <summary>https://api.bybit.com or https://api-testnet.bybit.com</summary>
    public string BaseUrl { get; set; } = "https://api.bybit.com";

    /// <summary>V5 category. Only linear is implemented.</summary>
    public string Category { get; set; } = "linear";

    public int HttpTimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 3;
    public int RecvWindowMs { get; set; } = 5_000;

    /// <summary>
    /// Use Bybit's private WebSocket to wake the trading loop promptly after
    /// an order, execution, or position update. REST still reconciles state.
    /// Disable temporarily if a network path blocks WebSockets.
    /// </summary>
    public bool PrivateWebSocketEnabled { get; set; } = true;

    /// <summary>
    /// Maintain a local 50-level linear order book from Bybit's public stream.
    /// REST remains an automatic fallback when the stream is unavailable/stale.
    /// </summary>
    public bool PublicOrderBookWebSocketEnabled { get; set; } = true;

    /// <summary>Maximum age of a streamed book before the client falls back to REST.</summary>
    public int PublicOrderBookFreshnessMs { get; set; } = 3_000;

    /// <summary>
    /// Per-venue override for Trading.PostOnlyEntries. Null = use the shared value.
    ///
    /// The right answer genuinely differs by venue. WhiteBIT charges 0.1% maker
    /// and 0.1% taker, so resting an order earns no discount and only risks not
    /// filling. Bybit charges 0.036% maker against 0.100% taker, which turns a
    /// round trip from 0.200% into 0.136% - removing a third of the fee drag on
    /// a strategy whose edge the fees were already eating.
    /// </summary>
    public bool? PostOnlyEntries { get; set; }

    /// <summary>
    /// Must start empty: the configuration binder appends to a pre-populated
    /// list instead of replacing it. Symbols use Bybit form (SOLUSDT).
    /// When empty and ExchangeMode includes Bybit, Trading.Markets are mapped
    /// by stripping underscores (SOL_USDT → SOLUSDT).
    /// </summary>
    public List<string> Markets { get; set; } = new();
}

public sealed class TradingConfig
{
    /// <summary>When true, decisions are logged but no orders are sent. Default is live.</summary>
    public bool DryRun { get; set; }

    /// <summary>Kept for older configs. Live no longer requires this flag.</summary>
    public bool IUnderstandThisTradesRealMoney { get; set; } = true;

    /// <summary>
    /// WhiteBIT | Bybit | Both. Empty / omitted = WhiteBIT so existing deploys
    /// keep a single WhiteBIT runner with Api.* credentials.
    /// </summary>
    public string ExchangeMode { get; set; } = "WhiteBIT";

    /// <summary>
    /// Must start empty: the configuration binder appends to a pre-populated
    /// list instead of replacing it, which silently duplicates every default.
    /// WhiteBIT symbols (SOL_USDT). Ignored for Bybit-only mode when Bybit.Markets is set.
    /// </summary>
    public List<string> Markets { get; set; } = new();

    /// <summary>Account-wide collateral leverage requested at startup.</summary>
    public int Leverage { get; set; } = 3;

    /// <summary>Candle interval used for entry signals.</summary>
    public string EntryInterval { get; set; } = "15m";

    /// <summary>Higher timeframe used only as a regime/direction filter.</summary>
    public string RegimeInterval { get; set; } = "1h";

    public int CandleLookback { get; set; } = 100;

    /// <summary>Main decision loop period.</summary>
    public int LoopIntervalMs { get; set; } = 2_000;

    /// <summary>
    /// How often to emit the equity / blocked-setup status lines.
    /// 1 = every tick. Keep small enough that Railway logs feel alive.
    /// </summary>
    public int StatusLogEveryTicks { get; set; } = 5;

    /// <summary>Stop distance as a multiple of ATR on the entry timeframe.</summary>
    public decimal StopLossAtrMult { get; set; } = 1.5m;

    /// <summary>Take-profit distance as a multiple of ATR on the entry timeframe.</summary>
    public decimal TakeProfitAtrMult { get; set; } = 3.5m;

    /// <summary>
    /// The exchange-side stop attached to the entry order, as a multiple of the stop the bot manages.
    /// Deliberately wider: it is the backstop for when the bot dies, not the working stop.
    /// </summary>
    public decimal HardStopMultiplier { get; set; } = 1.8m;

    /// <summary>
    /// Floor for the break-even trigger. The effective trigger is
    /// max(this, feeCushionR + BreakEvenBufferR), because a break-even stop
    /// physically cannot sit below entry + the round-trip fee.
    /// </summary>
    public decimal BreakEvenAtR { get; set; } = 0m;

    /// <summary>
    /// Whether the stop moves to break-even at all.
    ///
    /// Measured off: the entry signal's edge appears at an 8-16 bar horizon
    /// (+0.48% mean at 8 bars), but a break-even stop at entry+fees closes a
    /// third of trades near zero long before that. Disabling it moved gross PnL
    /// from -1.38 to +14.71 across three independent 45-day windows, positive in
    /// all three. It reduces the protection on each trade, which is why the hold
    /// is capped by MaxPositionMinutes instead.
    /// </summary>
    public bool BreakEvenEnabled { get; set; } = true;

    /// <summary>
    /// How far past the fee cushion the trade must travel before the stop moves
    /// to break-even. Too small and normal noise stops the trade out at zero;
    /// too large and break-even never arms.
    /// </summary>
    public decimal BreakEvenBufferR { get; set; } = 0.40m;

    /// <summary>Start trailing once unrealised profit reaches this multiple of the stop distance. 0 disables trailing.</summary>
    public decimal TrailStartAtR { get; set; } = 2.0m;

    /// <summary>Trailing distance as a multiple of ATR.</summary>
    public decimal TrailAtrMult { get; set; } = 1.3m;

    /// <summary>Arm a give-back exit once the trade has reached this R. 0 disables — let the target or trail work.</summary>
    public decimal GiveBackArmR { get; set; }

    /// <summary>Close if the trade falls this far in R from its peak after GiveBackArmR.</summary>
    public decimal GiveBackDropR { get; set; } = 0.80m;

    /// <summary>Force-close a position open longer than this. Scalps should not become investments.</summary>
    public int MaxPositionMinutes { get; set; } = 360;

    /// <summary>How many new entries to place in a single loop after ranking. 1 is conservative.</summary>
    public int MaxEntriesPerTick { get; set; } = 1;

    /// <summary>
    /// Close a trade that has never gone positive after this many minutes.
    /// 0 disables. Stops a losing adopted/stale position from waiting out the time stop.
    /// </summary>
    public int ScratchUnprofitableMinutes { get; set; } = 90;

    /// <summary>
    /// Only scratch a stale trade whose best excursion stayed below this. A
    /// trade that reached further has an armed break-even stop and should be
    /// left to it rather than closed at a guaranteed round-trip fee.
    /// </summary>
    public decimal ScratchMaxFavourableR { get; set; } = 0.5m;

    /// <summary>
    /// Only scratch while the trade is still near flat: R must be above this.
    /// A trade already at -0.7R has 0.3R left to the stop and the whole
    /// recovery to gain, so it is left to the stop. -1 disables the gate.
    /// </summary>
    public decimal ScratchOnlyAboveR { get; set; } = -0.35m;

    /// <summary>
    /// With nothing open and nothing pending, the equity status line is
    /// logged at most this often (minutes). Position lines keep the tick
    /// cadence. 0 = every StatusLogEveryTicks.
    /// </summary>
    public int IdleStatusLogMinutes { get; set; } = 5;

    /// <summary>
    /// true  = rest a post-only limit at the near touch; pays no spread but may
    ///         never fill. Worth it only when maker fees are below taker fees.
    /// false = quote the far touch so the order fills immediately; slippage is
    ///         capped at the spread, which MaxSpreadBps already limits.
    /// WhiteBIT charges 0.1% both sides on the majors, so resting buys nothing.
    /// </summary>
    public bool PostOnlyEntries { get; set; } = false;

    /// <summary>Cancel an unfilled entry order after this many seconds.</summary>
    public int EntryOrderTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Extra entry filter used only for post-only orders. It rejects passive
    /// orders that would join a thin/hostile queue instead of assuming maker
    /// fees alone make every resting order attractive.
    /// </summary>
    public bool MakerQualityEnabled { get; set; } = true;

    /// <summary>Minimum signed top-book imbalance in the intended direction.</summary>
    public decimal MakerMinDirectionalImbalance { get; set; } = 0.05m;

    /// <summary>Minimum quote liquidity resting at the intended best price. Zero disables.</summary>
    public decimal MakerMinTopLevelDepthUsd { get; set; } = 100m;

    /// <summary>Maximum fraction of the intended touch queue occupied by our order.</summary>
    public decimal MakerMaxQueueFraction { get; set; } = 0.20m;

    /// <summary>Refuse a passive order when its source book is older than this.</summary>
    public int MakerMaxBookAgeMs { get; set; } = 2_500;

    /// <summary>Refuse a passive order if scan-to-submit latency exceeds this.</summary>
    public int MakerMaxSignalAgeMs { get; set; } = 2_000;

    /// <summary>
    /// Warn when passive entries are being cancelled too often. This is only
    /// used when PostOnlyEntries is enabled; zero disables the alert.
    /// </summary>
    public int PostOnlyFillWarningMinAttempts { get; set; } = 5;

    /// <summary>Minimum acceptable post-only fill rate, expressed as 0-100.</summary>
    public decimal PostOnlyFillWarningMinRatePct { get; set; } = 40m;

    /// <summary>Rolling window used by the post-only fill-rate warning.</summary>
    public int PostOnlyFillWarningWindowMinutes { get; set; } = 180;

    /// <summary>
    /// Take part of the position off once the trade reaches this R (0 disables).
    /// The remainder gets a break-even+fees stop and keeps running to the target
    /// or trail, so the win rate rises without giving up the long tail.
    /// </summary>
    public decimal PartialTakeAtR { get; set; } = 0m;

    /// <summary>Fraction of the position closed at PartialTakeAtR. Both halves must clear the market minimums.</summary>
    public decimal PartialTakeFraction { get; set; } = 0.5m;

    /// <summary>
    /// UTC hour window for NEW entries, inclusive start, exclusive end. Equal
    /// values disable the filter. Open positions are managed at any hour.
    /// Alt books on WhiteBIT are thin outside EU/US hours and spreads widen.
    /// </summary>
    public int EntryHoursUtcStart { get; set; } = 0;
    public int EntryHoursUtcEnd { get; set; } = 0;

    /// <summary>
    /// JSON-lines file with one record per closed trade, including the entry
    /// context (components, ATR%, funding, hour, BTC regime). Empty disables.
    /// The daily Telegram digest is built from this file.
    /// </summary>
    public string TradeJournalPath { get; set; } = "data/trades.jsonl";

    /// <summary>
    /// Human-assigned label for a frozen strategy/configuration experiment. It
    /// is stamped on every shadow signal and closed trade so results from a
    /// changed strategy are never silently mixed with the prior one.
    /// </summary>
    public string ExperimentId { get; set; } = "baseline-v1";

    /// <summary>
    /// Append one closed-bar signal evaluation per market, including vetoed
    /// setups. This is observation only: it never changes entry decisions.
    /// Empty ShadowSignalPath disables the journal.
    /// </summary>
    public bool ShadowSignalsEnabled { get; set; } = true;

    public string ShadowSignalPath { get; set; } = "data/shadow-signals.jsonl";

    /// <summary>
    /// Where per-position management state (verified opening time, stops,
    /// high-water mark, partial-take flag) is kept so a restart resumes the
    /// same trade instead of re-deriving it and resetting its age. Mount a
    /// volume here to survive redeploys. Empty disables.
    /// </summary>
    public string PositionStatePath { get; set; } = "data/positions.json";
}

public sealed class SignalConfig
{
    public int EmaFast { get; set; } = 9;
    public int EmaSlow { get; set; } = 21;
    public int EmaRegime { get; set; } = 50;
    public int RsiPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;
    public int AdxPeriod { get; set; } = 14;
    public int BollingerPeriod { get; set; } = 20;
    public double BollingerStdDev { get; set; } = 2.0;
    public int VolumeMaPeriod { get; set; } = 20;

    public int MacdFast { get; set; } = 12;
    public int MacdSlow { get; set; } = 26;
    public int MacdSignal { get; set; } = 9;
    public int StochPeriod { get; set; } = 14;
    public int StochSmoothK { get; set; } = 3;
    public int StochSmoothD { get; set; } = 3;
    public double StochOversold { get; set; } = 20;
    public double StochOverbought { get; set; } = 80;
    public int DonchianPeriod { get; set; } = 20;

    /// <summary>Weighted confluence score a setup must reach before it is tradable.</summary>
    public double MinScore { get; set; } = 0.58;

    /// <summary>Minimum number of independent components that must agree with the direction.</summary>
    public int MinConfirmations { get; set; } = 3;

    /// <summary>
    /// Minimum weight from event triggers (RSI, Bollinger, volume, book).
    /// Background state (regime / EMA / VWAP) cannot open a trade on its own.
    /// </summary>
    public double MinTriggerWeight { get; set; } = 0.12;


    /// <summary>
    /// Absolute weight that must back the chosen direction, out of the 1.00
    /// total across all components. Guards against a high `score` that only
    /// reflects agreement among a few components while the heavyweights
    /// (regime, rsi) abstain.
    /// </summary>
    public double MinAgreeingWeight { get; set; } = 0.42;

    /// <summary>
    /// Minimum |agreeing − opposing| weight. Abstains when both sides have
    /// real support even if the agreement score clears MinScore — the
    /// confidence-gating idea from calibrated decision engines, applied to
    /// the confluence battery. 0 disables.
    /// </summary>
    public double MinDirectionEdge { get; set; } = 0.18;

    /// <summary>Refuse an entry when the last closed bar's volume is this far below its average.</summary>
    public double MinVolumeRatio { get; set; } = 0.40;

    /// <summary>In a trend, do not buy when RSI is already this extended — wait for a pullback.</summary>
    public double MaxTrendRsiLong { get; set; } = 65;

    /// <summary>In a trend, do not sell when RSI is already this extended — wait for a bounce.</summary>
    public double MinTrendRsiShort { get; set; } = 35;

    /// <summary>
    /// When true, a long requires a non-negative MACD histogram and a short a
    /// non-positive one. Disable for more activity when other filters already agree.
    /// </summary>
    public bool RequireMacdAlign { get; set; } = true;

    /// <summary>
    /// When true, a flat 1h EMA50 blocks every entry. When false (default),
    /// only a decided opposite 1h is a veto — chop can still mean-revert.
    /// </summary>
    public bool RequireDecidedHtf { get; set; }

    /// <summary>
    /// Book must lean this hard against the setup before it vetoes.
    /// Below this the book can still vote, but a 12% flicker will not kill a 15m signal.
    /// </summary>
    public double BookVetoMinImbalance { get; set; } = 0.20;

    /// <summary>
    /// Stand aside on crowded perp funding or a fresh BTC 1h flip against the alt.
    /// These are vetoes, not votes — they never open a trade.
    /// </summary>
    public bool StandAsideEnabled { get; set; } = true;

    /// <summary>Spot market whose 1h regime is the risk-on/off switch. Usually BTC_USDT.</summary>
    public string AnchorMarket { get; set; } = "BTC_USDT";

    /// <summary>Interval for the anchor regime. Empty uses Trading.RegimeInterval.</summary>
    public string AnchorInterval { get; set; } = "1h";

    /// <summary>
    /// Predicted perp funding (decimal, 0.0002 = 0.02%) at which we refuse the
    /// crowded side. Longs pay when funding is positive. 0 disables the funding veto.
    /// </summary>
    public decimal MaxFundingRate { get; set; } = 0.0002m;

    /// <summary>ADX above this counts as a trending regime; below it, as chop.</summary>
    public double AdxTrendThreshold { get; set; } = 22;

    public double RsiOversold { get; set; } = 40;
    public double RsiOverbought { get; set; } = 60;
    public double RsiExtremeHigh { get; set; } = 78;
    public double RsiExtremeLow { get; set; } = 22;

    /// <summary>Top-of-book depth levels used for the order-book imbalance component.</summary>
    public int OrderBookDepth { get; set; } = 10;

    public double ImbalanceThreshold { get; set; } = 0.12;

    // Veto filters
    /// <summary>Refuse to trade when the book spread is wider than this, in basis points.</summary>
    public decimal MaxSpreadBps { get; set; } = 10m;

    /// <summary>
    /// Refuse to trade when ATR% of price is below this.
    ///
    /// This is the single highest-impact filter in the strategy. A 0.2% round
    /// trip costs fee/(stopMult*ATR%) in R terms, so low-volatility entries pay
    /// the same fee for a smaller move. Measured over three independent 45-day
    /// windows on the same six markets, raising this from 0.05 to 0.60:
    ///   May-Jun  -9.64 -> -1.37   (52.1% -> 62.2% win rate)
    ///   Jun-Aug -11.38 -> +3.54   (48.5% -> 71.0%)
    ///   Aug-Sep -13.47 -> -0.18   (46.6% -> 54.2%)
    /// It cuts trade count by ~70% and fees by ~80% while raising gross profit.
    /// </summary>
    public decimal MinAtrPct { get; set; } = 0.35m;

    /// <summary>Refuse to trade when ATR% of price is above this - news or panic volatility.</summary>
    public decimal MaxAtrPct { get; set; } = 3.5m;

    /// <summary>
    /// Trend-pullback trigger: in an ADX trend, a bar that tags the slow EMA
    /// and closes back on the trend side with RSI in the healthy band votes.
    /// The only way into a strong trend without chasing an extended RSI.
    /// Off by default: on 45/90-day replays (Aug-Sep 2026, 4 alts) the extra
    /// trades were net negative at vol 1.0x and break-even at 1.3x. Turn on
    /// when the journal shows trend days being missed, not before.
    /// </summary>
    public bool PullbackTriggerEnabled { get; set; } = false;

    /// <summary>Resumption bar volume vs its MA required for the pullback trigger to vote.</summary>
    public decimal PullbackMinVolumeRatio { get; set; } = 1.3m;

    /// <summary>
    /// Refuse to trade when the thinner side of the book (top OrderBookDepth
    /// levels) holds less than this many USDT. A depth check catches an
    /// illiquid market on any name before the spread veto does. 0 disables.
    /// </summary>
    public decimal MinBookDepthUsd { get; set; } = 0m;
}

public sealed class RiskConfig
{
    /// <summary>Equity fraction risked per trade, in percent, measured to the bot stop.</summary>
    public decimal RiskPerTradePct { get; set; } = 0.25m;

    /// <summary>
    /// Informational target for the current risk-ramp stage. This does not
    /// size orders; RiskPerTradePct remains the live value. Keeping it separate
    /// makes a 2% ceiling visible without accidentally jumping a deployed bot
    /// to it before enough journal evidence exists.
    /// </summary>
    public decimal TargetRiskPerTradePct { get; set; } = 2m;

    /// <summary>Hard ceiling on position notional, in quote currency.</summary>
    public decimal MaxNotionalPerPositionUsd { get; set; } = 40m;

    /// <summary>Hard ceiling on the sum of all open position notionals.</summary>
    public decimal MaxTotalNotionalUsd { get; set; } = 80m;

    public int MaxConcurrentPositions { get; set; } = 1;

    /// <summary>Cap on longs or shorts at once. Crypto alts move together; two longs is one bet.</summary>
    public int MaxSameDirectionPositions { get; set; } = 1;

    /// <summary>One position per market at a time.</summary>
    public bool OnePositionPerMarket { get; set; } = true;

    /// <summary>Session halts (and flattens) when equity falls this far below the session starting equity (0 disables).</summary>
    public decimal MaxDailyDrawdownPct { get; set; } = 2m;

    /// <summary>
    /// Halt when equity falls this far below the session peak (0 disables).
    /// Catches give-back after a green streak that session-start drawdown misses.
    /// </summary>
    public decimal MaxPeakDrawdownPct { get; set; } = 2m;

    /// <summary>
    /// Halt when session equity profit reaches this percent of starting equity
    /// (0 disables). Locks a small green day instead of giving it back.
    /// </summary>
    public decimal MaxDailyProfitPct { get; set; } = 1.5m;

    /// <summary>
    /// What the profit lock does. 0 = halt for the UTC day (original
    /// behaviour). Greater than 0 = keep trading with RiskPerTradePct and the
    /// notional caps scaled by this factor for the rest of the day, so a green
    /// day can still compound while a give-back stays small. Peak drawdown
    /// still protects the gains.
    /// </summary>
    public decimal ProfitLockRiskScale { get; set; } = 0m;

    /// <summary>
    /// Risk scale applied on Saturday and Sunday UTC (1 = no change).
    /// Weekend crypto volume is thinner and moves are more often fee-only.
    /// </summary>
    public decimal WeekendRiskScale { get; set; } = 1m;

    /// <summary>
    /// Floor for conviction-scaled sizing. 1 = always full RiskPerTradePct
    /// (disabled). Below 1, size scales linearly from this floor at MinScore
    /// up to 1.0 at score=1. Mirror of "gate size on calibrated confidence":
    /// weak setups risk less, strong ones keep the full budget. Never raises
    /// size above RiskPerTradePct.
    /// </summary>
    public decimal ConvictionRiskFloor { get; set; } = 1m;

    /// <summary>Stop opening new positions after this many losses in a row.</summary>
    public int MaxConsecutiveLosses { get; set; } = 10;

    /// <summary>
    /// When true, hitting MaxConsecutiveLosses flattens the session (halt).
    /// When false, only a cooldown of LossCooldownMinutes is applied.
    /// </summary>
    public bool HaltOnConsecutiveLosses { get; set; } = true;

    /// <summary>Cooldown after the consecutive-loss limit is hit, in minutes (used when HaltOnConsecutiveLosses is false).</summary>
    public int LossCooldownMinutes { get; set; } = 60;

    /// <summary>
    /// Pause only one symbol after this many consecutive realised losses. This
    /// limits a market-specific regime failure without stopping protection for
    /// positions or disabling unrelated symbols. Zero disables the brake.
    /// </summary>
    public int MaxConsecutiveLossesPerMarket { get; set; } = 3;

    /// <summary>How long a symbol stays closed after its per-market loss brake triggers.</summary>
    public int MarketLossCooldownMinutes { get; set; } = 360;

    /// <summary>Minimum wait between two entries on the same market, in seconds.</summary>
    public int PerMarketCooldownSeconds { get; set; } = 300;

    /// <summary>Maximum entries per hour across all markets.</summary>
    public int MaxTradesPerHour { get; set; } = 6;

    /// <summary>Maximum entries per UTC day across all markets.</summary>
    public int MaxTradesPerDay { get; set; } = 16;

    /// <summary>
    /// Fallback round-trip fee, in percent of notional, used only when the
    /// exchange reports no fee for a market. Normally the real maker/taker fees
    /// from /api/v4/public/markets are used instead.
    /// </summary>
    public decimal RoundTripFeePct { get; set; } = 0.2m;

    /// <summary>Required net profit after fees at the take-profit level, in percent of notional.</summary>
    public decimal MinNetEdgePct { get; set; } = 0.15m;

    /// <summary>Refuse a setup whose stop is tighter than this percent of price.</summary>
    public decimal MinStopPct { get; set; } = 0.45m;

    /// <summary>Stop must also be at least this many times the round-trip fee.</summary>
    public decimal MinStopFeeMultiple { get; set; } = 2.5m;

    /// <summary>
    /// Net PnL inside this band is classified as fee-sized. Such losses DO count
    /// toward the loss streak and its cumulative total (they are reported
    /// separately); a fee-sized win does not reset a losing streak.
    /// </summary>
    public decimal FeeNoiseUsd { get; set; } = 0.12m;

    /// <summary>Close everything and halt when the account reports margin_call or liquidation state.</summary>
    public bool FlattenOnMarginCall { get; set; } = true;

    /// <summary>
    /// When collateral contains assets other than the markets' quote currency,
    /// exclude their marked value from bot sizing and PnL locks. This stops an
    /// ETH/BTC price move being mistaken for strategy PnL. It does not remove
    /// those assets from the exchange cross-margin account or eliminate their
    /// liquidation risk.
    /// </summary>
    public bool ExcludeNonQuoteCollateralFromRiskEquity { get; set; } = true;

    /// <summary>How often public prices refresh for non-quote collateral valuation.</summary>
    public int CollateralValuationRefreshSeconds { get; set; } = 10;

    /// <summary>
    /// Persist halt / loss-streak / daily entry counters to disk so a process
    /// restart (Railway ALWAYS policy, crash, redeploy) cannot wipe a hard halt.
    /// </summary>
    public bool PersistSessionState { get; set; } = true;

    /// <summary>
    /// Path for the session-state file. Default is under the app directory.
    /// Mount a Railway volume here if you want the halt to survive redeploys.
    /// </summary>
    public string SessionStatePath { get; set; } = "data/session-state.json";

    /// <summary>
    /// When true on boot, delete any persisted halt and start a fresh session.
    /// Set TRADER_Risk__ClearHaltOnBoot=true after reviewing why it halted.
    /// </summary>
    public bool ClearHaltOnBoot { get; set; }
}

/// <summary>
/// Telegram notifications. The bot token is a credential: keep it out of
/// appsettings.json and supply it via TRADER_Telegram__BotToken or
/// TELEGRAM_BOT_TOKEN, the same way as the exchange keys.
///
/// Note that enabling this sends your trading activity - markets, sizes,
/// prices and PnL - to Telegram's servers.
/// </summary>
public sealed class TelegramConfig
{
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = "";

    /// <summary>Numeric chat id. Run with --telegram-test to discover it.</summary>
    public string ChatId { get; set; } = "";

    /// <summary>
    /// Short summaries only: position opened, position closed with profit or
    /// loss and the resulting balance, and the periodic status.
    /// </summary>
    public bool SendTrades { get; set; } = true;

    /// <summary>
    /// Forward every raw log line - signal breakdowns, order ids, stop moves.
    /// Useful for debugging, far too noisy for a phone.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>Circuit breakers, halts, margin calls, forced flattening.</summary>
    public bool SendRisk { get; set; } = true;

    public bool SendErrors { get; set; } = true;

    /// <summary>Warnings are noisier and usually not actionable from a phone.</summary>
    public bool SendWarnings { get; set; }

    /// <summary>Periodic status message with equity and open positions. 0 disables.</summary>
    public int HeartbeatMinutes { get; set; } = 60;

    /// <summary>
    /// Accept commands (/status, /restart, ...) sent to the bot.
    ///
    /// Only messages from the configured ChatId are acted on - a Telegram bot is
    /// reachable by anyone who finds its handle. Long polling also conflicts
    /// across processes, so exactly one instance may have this enabled per token.
    /// </summary>
    public bool CommandsEnabled { get; set; } = true;

    /// <summary>One summary of the previous UTC day's trades at midnight, from the trade journal.</summary>
    public bool DailyDigest { get; set; } = true;

    /// <summary>
    /// GET this URL on a timer (healthchecks.io ping URL). If the process dies
    /// without a SIGTERM, pings stop and the watchdog sends Telegram — the bot
    /// itself cannot. Empty disables. HEALTHCHECK_PING_URL also works.
    /// </summary>
    public string WatchdogPingUrl { get; set; } = "";

    /// <summary>How often to ping the watchdog. 0 uses HeartbeatMinutes (min 1).</summary>
    public int WatchdogPingMinutes { get; set; } = 5;

    /// <summary>
    /// Minimum gap between outbound messages. Telegram throttles aggressively per
    /// chat; queued messages are batched into one send rather than dropped.
    /// </summary>
    public int MinSendGapMs { get; set; } = 1_200;
}
