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
    /// <summary>When true, decisions are logged but no orders are sent. Default is live.</summary>
    public bool DryRun { get; set; }

    /// <summary>Kept for older configs. Live no longer requires this flag.</summary>
    public bool IUnderstandThisTradesRealMoney { get; set; } = true;

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
}

public sealed class RiskConfig
{
    /// <summary>Equity fraction risked per trade, in percent, measured to the bot stop.</summary>
    public decimal RiskPerTradePct { get; set; } = 0.25m;

    /// <summary>Hard ceiling on position notional, in quote currency.</summary>
    public decimal MaxNotionalPerPositionUsd { get; set; } = 40m;

    /// <summary>Hard ceiling on the sum of all open position notionals.</summary>
    public decimal MaxTotalNotionalUsd { get; set; } = 80m;

    public int MaxConcurrentPositions { get; set; } = 1;

    /// <summary>Cap on longs or shorts at once. Crypto alts move together; two longs is one bet.</summary>
    public int MaxSameDirectionPositions { get; set; } = 1;

    /// <summary>One position per market at a time.</summary>
    public bool OnePositionPerMarket { get; set; } = true;

    /// <summary>Session halts (and flattens) when equity falls this far below the session starting equity.</summary>
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

    /// <summary>Stop opening new positions after this many losses in a row.</summary>
    public int MaxConsecutiveLosses { get; set; } = 10;

    /// <summary>
    /// When true, hitting MaxConsecutiveLosses flattens the session (halt).
    /// When false, only a cooldown of LossCooldownMinutes is applied.
    /// </summary>
    public bool HaltOnConsecutiveLosses { get; set; } = true;

    /// <summary>Cooldown after the consecutive-loss limit is hit, in minutes (used when HaltOnConsecutiveLosses is false).</summary>
    public int LossCooldownMinutes { get; set; } = 60;

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

    /// <summary>Net PnL inside this band is fee noise and does not count toward the loss streak.</summary>
    public decimal FeeNoiseUsd { get; set; } = 0.12m;

    /// <summary>Close everything and halt when the account reports margin_call or liquidation state.</summary>
    public bool FlattenOnMarginCall { get; set; } = true;

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
