namespace TraderApp.Configuration;

/// <summary>
/// Root configuration. Bound from appsettings.json, then overridden by
/// environment variables (prefix TRADER_, e.g. TRADER_Api__Key).
/// Secrets must never live in appsettings.json - see README.
/// </summary>
public sealed class BotConfig
{
    public ApiConfig Api { get; set; } = new();
    public TradingConfig Trading { get; set; } = new();
    public SignalConfig Signals { get; set; } = new();
    public RiskConfig Risk { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Api.Key))
            errors.Add("Api.Key is empty. Set TRADER_Api__Key or WHITEBIT_API_KEY.");
        if (string.IsNullOrWhiteSpace(Api.Secret))
            errors.Add("Api.Secret is empty. Set TRADER_Api__Secret or WHITEBIT_API_SECRET.");
        if (Trading.Markets.Count == 0)
            errors.Add("Trading.Markets is empty.");
        if (Trading.Leverage is < 1 or > 20)
            errors.Add("Trading.Leverage must be between 1 and 20.");
        if (Risk.RiskPerTradePct is <= 0 or > 2m)
            errors.Add("Risk.RiskPerTradePct must be in (0, 2]. More than 2% of equity per trade is not safe scalping.");
        if (Risk.MaxDailyDrawdownPct is <= 0 or > 20m)
            errors.Add("Risk.MaxDailyDrawdownPct must be in (0, 20].");
        if (Risk.MaxConcurrentPositions is < 1 or > 10)
            errors.Add("Risk.MaxConcurrentPositions must be between 1 and 10.");
        if (Signals.MinScore is < 0.3 or > 1.0)
            errors.Add("Signals.MinScore must be in [0.3, 1.0].");
        if (Trading.TakeProfitAtrMult <= Trading.StopLossAtrMult * 0.8m)
            errors.Add("Trading.TakeProfitAtrMult should be at least 0.8x StopLossAtrMult, otherwise the win rate required to break even is unrealistic.");
        if (Telegram.Enabled && string.IsNullOrWhiteSpace(Telegram.BotToken))
            errors.Add("Telegram.Enabled is true but Telegram.BotToken is empty. Set TRADER_Telegram__BotToken or TELEGRAM_BOT_TOKEN.");
        if (Telegram.Enabled && !string.IsNullOrWhiteSpace(Telegram.ChatId)
                             && !Notifications.TelegramNotifier.LooksLikeChatId(Telegram.ChatId))
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

public sealed class TradingConfig
{
    /// <summary>
    /// When true the bot computes and logs every decision but sends no order-placing
    /// or position-closing request. This is the default and must be turned off deliberately.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Extra confirmation required alongside DryRun=false. Both must be set to trade live.</summary>
    public bool IUnderstandThisTradesRealMoney { get; set; }

    /// <summary>
    /// Must start empty: the configuration binder appends to a pre-populated
    /// list instead of replacing it, which silently duplicates every default.
    /// </summary>
    public List<string> Markets { get; set; } = new();

    /// <summary>Account-wide collateral leverage requested at startup.</summary>
    public int Leverage { get; set; } = 3;

    /// <summary>Candle interval used for entry signals.</summary>
    public string EntryInterval { get; set; } = "15m";

    /// <summary>Higher timeframe used only as a regime/direction filter.</summary>
    public string RegimeInterval { get; set; } = "4h";

    public int CandleLookback { get; set; } = 200;

    /// <summary>Main decision loop period.</summary>
    public int LoopIntervalMs { get; set; } = 3_000;

    /// <summary>Stop distance as a multiple of ATR on the entry timeframe.</summary>
    public decimal StopLossAtrMult { get; set; } = 1.4m;

    /// <summary>Take-profit distance as a multiple of ATR on the entry timeframe.</summary>
    public decimal TakeProfitAtrMult { get; set; } = 3.0m;

    /// <summary>
    /// The exchange-side stop attached to the entry order, as a multiple of the stop the bot manages.
    /// Deliberately wider: it is the backstop for when the bot dies, not the working stop.
    /// </summary>
    public decimal HardStopMultiplier { get; set; } = 1.8m;

    /// <summary>Move the stop to break-even plus fees once unrealised profit reaches this multiple of the stop distance.</summary>
    public decimal BreakEvenAtR { get; set; } = 1.3m;

    /// <summary>Start trailing once unrealised profit reaches this multiple of the stop distance. 0 disables trailing.</summary>
    public decimal TrailStartAtR { get; set; } = 1.8m;

    /// <summary>Trailing distance as a multiple of ATR.</summary>
    public decimal TrailAtrMult { get; set; } = 1.3m;

    /// <summary>Force-close a position open longer than this. Scalps should not become investments.</summary>
    public int MaxPositionMinutes { get; set; } = 180;

    /// <summary>
    /// Close a trade that has never gone positive after this many minutes.
    /// 0 disables. Stops a losing adopted/stale position from waiting out the time stop.
    /// </summary>
    public int ScratchUnprofitableMinutes { get; set; } = 60;

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

    /// <summary>Weighted confluence score a setup must reach before it is tradable.</summary>
    public double MinScore { get; set; } = 0.62;

    /// <summary>Minimum number of independent components that must agree with the direction.</summary>
    public int MinConfirmations { get; set; } = 3;

    /// <summary>
    /// Minimum weight from event triggers (RSI, Bollinger, volume, book).
    /// Background state (regime / EMA / VWAP) cannot open a trade on its own.
    /// </summary>
    public double MinTriggerWeight { get; set; } = 0.14;

    /// <summary>Refuse an entry when the last closed bar's volume is this far below its average.</summary>
    public double MinVolumeRatio { get; set; } = 0.40;

    /// <summary>In a trend, do not buy when RSI is already this extended — wait for a pullback.</summary>
    public double MaxTrendRsiLong { get; set; } = 60;

    /// <summary>In a trend, do not sell when RSI is already this extended — wait for a bounce.</summary>
    public double MinTrendRsiShort { get; set; } = 40;

    /// <summary>ADX above this counts as a trending regime; below it, as chop.</summary>
    public double AdxTrendThreshold { get; set; } = 22;

    public double RsiOversold { get; set; } = 35;
    public double RsiOverbought { get; set; } = 65;
    public double RsiExtremeHigh { get; set; } = 78;
    public double RsiExtremeLow { get; set; } = 22;

    /// <summary>Top-of-book depth levels used for the order-book imbalance component.</summary>
    public int OrderBookDepth { get; set; } = 10;

    public double ImbalanceThreshold { get; set; } = 0.18;

    // Veto filters
    /// <summary>Refuse to trade when the book spread is wider than this, in basis points.</summary>
    public decimal MaxSpreadBps { get; set; } = 8m;

    /// <summary>Refuse to trade when ATR% of price is below this - no range to scalp, fees dominate.</summary>
    public decimal MinAtrPct { get; set; } = 0.05m;

    /// <summary>Refuse to trade when ATR% of price is above this - news or panic volatility.</summary>
    public decimal MaxAtrPct { get; set; } = 1.2m;
}

public sealed class RiskConfig
{
    /// <summary>Equity fraction risked per trade, in percent, measured to the bot stop.</summary>
    public decimal RiskPerTradePct { get; set; } = 0.25m;

    /// <summary>Hard ceiling on position notional, in quote currency.</summary>
    public decimal MaxNotionalPerPositionUsd { get; set; } = 200m;

    /// <summary>Hard ceiling on the sum of all open position notionals.</summary>
    public decimal MaxTotalNotionalUsd { get; set; } = 600m;

    public int MaxConcurrentPositions { get; set; } = 2;

    /// <summary>One position per market at a time.</summary>
    public bool OnePositionPerMarket { get; set; } = true;

    /// <summary>Session halts (and flattens) when equity falls this far below the session starting equity.</summary>
    public decimal MaxDailyDrawdownPct { get; set; } = 3m;

    /// <summary>Stop opening new positions after this many losses in a row.</summary>
    public int MaxConsecutiveLosses { get; set; } = 3;

    /// <summary>Cooldown after the consecutive-loss limit is hit, in minutes.</summary>
    public int LossCooldownMinutes { get; set; } = 30;

    /// <summary>Minimum wait between two entries on the same market, in seconds.</summary>
    public int PerMarketCooldownSeconds { get; set; } = 900;

    /// <summary>Maximum entries per hour across all markets.</summary>
    public int MaxTradesPerHour { get; set; } = 6;

    /// <summary>
    /// Fallback round-trip fee, in percent of notional, used only when the
    /// exchange reports no fee for a market. Normally the real maker/taker fees
    /// from /api/v4/public/markets are used instead.
    /// </summary>
    public decimal RoundTripFeePct { get; set; } = 0.2m;

    /// <summary>Required net profit after fees at the take-profit level, in percent of notional.</summary>
    public decimal MinNetEdgePct { get; set; } = 0.10m;

    /// <summary>Close everything and halt when the account reports margin_call or liquidation state.</summary>
    public bool FlattenOnMarginCall { get; set; } = true;
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
    /// Minimum gap between outbound messages. Telegram throttles aggressively per
    /// chat; queued messages are batched into one send rather than dropped.
    /// </summary>
    public int MinSendGapMs { get; set; } = 1_200;
}
