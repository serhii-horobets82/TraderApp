using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Trading;

namespace TraderApp.Risk;

public sealed record SizingResult(bool Allowed, decimal Amount, decimal StopPrice, decimal TakeProfitPrice,
    decimal HardStopPrice, string Reason);

/// <summary>
/// Every "can I" question the strategy must answer before it is allowed to
/// spend money. Kept separate from the strategy on purpose: the strategy can be
/// rewritten freely, these limits are the part that must not be.
/// </summary>
public sealed class RiskManager
{
    private readonly RiskConfig _cfg;
    private readonly TradingConfig _trading;
    private readonly SessionStateStore _store;

    private readonly Dictionary<string, DateTime> _lastEntryPerMarket = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _entryTimestamps = new();

    private int _consecutiveLosses;
    private int _consecutiveSmallLosses;
    private decimal _lossStreakUsd;
    private int _entriesToday;
    private DateOnly _entriesDay;
    private DateTime? _cooldownUntil;
    private DateTime? _haltedAtUtc;
    private HaltKind _haltKind;
    private DateOnly? _profitLockedDay;
    private bool _clearHaltOnBootConsumed;

    public decimal SessionStartEquity { get; private set; }

    /// <summary>True while the day's profit lock has switched the bot to reduced risk (ProfitLockRiskScale &gt; 0).</summary>
    public bool ProfitLocked => _profitLockedDay is { } d && d == DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Maps a confluence score onto [ConvictionRiskFloor, 1]. Score at or
    /// below minScore gets the floor; score 1.0 gets full risk. Returns 1
    /// when the floor is disabled (>= 1) so sizing is unchanged.
    /// </summary>
    public static decimal ConvictionScale(double score, double minScore, decimal floor)
    {
        if (floor >= 1m) return 1m;
        if (floor <= 0m) floor = 0m;
        if (score <= minScore) return floor;
        if (score >= 1.0) return 1m;
        var span = 1.0 - minScore;
        if (span <= 1e-9) return 1m;
        var t = (score - minScore) / span;
        return floor + (1m - floor) * (decimal)t;
    }

    /// <summary>
    /// Combined multiplier applied to RiskPerTradePct and the notional caps:
    /// profit lock (after the day's cap) times the weekend scale times an
    /// optional conviction scale from the signal score.
    /// </summary>
    public decimal RiskScale(DateTime? utcNow = null, decimal convictionScale = 1m)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var scale = 1m;
        if (ProfitLocked && _cfg.ProfitLockRiskScale > 0)
            scale *= _cfg.ProfitLockRiskScale;
        if (_cfg.WeekendRiskScale > 0 && _cfg.WeekendRiskScale < 1 &&
            now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            scale *= _cfg.WeekendRiskScale;
        if (convictionScale > 0 && convictionScale < 1m)
            scale *= convictionScale;
        return scale;
    }

    /// <summary>Whether a new entry may be placed at this UTC hour (open positions are always managed).</summary>
    public static bool InsideEntryHours(TradingConfig trading, DateTime utcNow)
    {
        var start = trading.EntryHoursUtcStart;
        var end = trading.EntryHoursUtcEnd;
        if (start == end) return true;
        var h = utcNow.Hour;
        return start < end ? h >= start && h < end : h >= start || h < end;
    }
    public decimal PeakEquity { get; private set; }
    public int ConsecutiveLosses => _consecutiveLosses;

    /// <summary>Of the current streak, how many losses were within FeeNoiseUsd. Included in <see cref="ConsecutiveLosses"/>.</summary>
    public int ConsecutiveSmallLosses => _consecutiveSmallLosses;

    /// <summary>Total realised loss of the current streak, small losses included.</summary>
    public decimal LossStreakUsd => _lossStreakUsd;
    public int EntriesToday => _entriesToday;
    public bool Halted { get; private set; }

    /// <summary>
    /// Manual pause. Blocks NEW entries only - open positions keep being
    /// managed, so stops, targets and the time stop all still fire. That is the
    /// difference between pausing and flipping DryRun: DryRun would also stop
    /// the bot closing what it already holds, leaving only the wider
    /// exchange-side stop between an open position and the market.
    /// </summary>
    public bool EntriesPaused { get; private set; }
    public DateTime? PausedAtUtc { get; private set; }
    public string HaltReason { get; private set; } = "";
    public HaltKind Kind => _haltKind;
    public bool HaltClearsOnUtcDay => HaltKindRules.ClearsOnUtcDay(_haltKind);

    public RiskManager(RiskConfig cfg, TradingConfig trading, SessionStateStore? store = null)
    {
        _cfg = cfg;
        _trading = trading;
        _store = store ?? new SessionStateStore(cfg);
        _entriesDay = DateOnly.FromDateTime(DateTime.UtcNow);
    }

    /// <summary>
    /// Restore a persisted halt / streak, or start a fresh equity session.
    /// Call once at boot after reading live equity.
    /// </summary>
    public void RestoreOrInitialise(decimal equity)
    {
        // ClearHaltOnBoot is intentionally one-shot. Railway environment
        // variables persist across deploys, so treating `true` as "clear on
        // every process start" silently bypasses hard loss halts. Operators
        // who need another deliberate reset must toggle it false, then true.
        var saved = _store.TryLoad();
        if (_cfg.ClearHaltOnBoot && saved?.ClearHaltOnBootConsumed != true)
        {
            _store.Clear();
            _clearHaltOnBootConsumed = true;
            Log.Risk("ClearHaltOnBoot=true — one-time reset of persisted session state");
            InitialiseEquity(equity);
            return;
        }

        if (_cfg.ClearHaltOnBoot && saved?.ClearHaltOnBootConsumed == true)
            Log.Risk("ClearHaltOnBoot=true was already consumed; restoring risk state. Toggle it false then true for another deliberate reset.");

        if (saved is null)
        {
            InitialiseEquity(equity);
            return;
        }

        _clearHaltOnBootConsumed = saved.ClearHaltOnBootConsumed;

        if (!string.IsNullOrEmpty(saved.EntriesDay) &&
            DateOnly.TryParse(saved.EntriesDay, out var day))
            _entriesDay = day;

        _consecutiveLosses = Math.Max(0, saved.ConsecutiveLosses);
        _consecutiveSmallLosses = Math.Clamp(saved.ConsecutiveSmallLosses, 0, _consecutiveLosses);
        _lossStreakUsd = Math.Max(0m, saved.LossStreakUsd);
        _entriesToday = Math.Max(0, saved.EntriesToday);

        if (saved.CooldownUntilUtc is { } until && until > DateTime.UtcNow)
            _cooldownUntil = until;

        if (!string.IsNullOrEmpty(saved.ProfitLockedDay) &&
            DateOnly.TryParse(saved.ProfitLockedDay, out var lockedDay))
            _profitLockedDay = lockedDay;

        if (saved.EntriesPaused)
        {
            EntriesPaused = true;
            PausedAtUtc = saved.PausedAtUtc ?? DateTime.UtcNow;
            Log.Risk("restored PAUSED state - no new entries until /resume");
        }

        if (saved.Halted)
        {
            Halted = true;
            HaltReason = string.IsNullOrWhiteSpace(saved.HaltReason)
                ? "restored session halt"
                : saved.HaltReason;
            _haltKind = HaltKindRules.Parse(saved.HaltKind);
            // Older state files had no kind — infer from the reason text.
            if (_haltKind == HaltKind.None)
                _haltKind = InferKind(HaltReason);
            _haltedAtUtc = saved.HaltedAtUtc ?? DateTime.UtcNow;

            // Limit was raised (e.g. 3 → 10): drop a consecutive-loss halt that
            // no longer meets the configured threshold.
            if (_haltKind == HaltKind.ConsecutiveLosses &&
                _consecutiveLosses < _cfg.MaxConsecutiveLosses)
            {
                Log.Risk($"cleared consecutive-loss halt on restore: streak {_consecutiveLosses} " +
                         $"< limit {_cfg.MaxConsecutiveLosses}");
                Halted = false;
                HaltReason = "";
                _haltKind = HaltKind.None;
                _haltedAtUtc = null;
            }
        }

        if (saved.SessionStartEquity > 0)
        {
            SessionStartEquity = saved.SessionStartEquity;
            PeakEquity = Math.Max(saved.PeakEquity, equity);
        }
        else
        {
            SessionStartEquity = equity;
            PeakEquity = equity;
        }

        // Roll the day after halt flags are in place so Persist cannot wipe them.
        TickUtcDay(equity);

        if (Halted)
        {
            Log.Risk($"restored HALTED state from {_store.FilePath}: {HaltReason} " +
                     $"(loss streak {_consecutiveLosses}, entries today {_entriesToday})");
        }
        else if (_consecutiveLosses > 0 || _entriesToday > 0)
        {
            Log.Info($"restored session counters from {_store.FilePath}: " +
                     $"loss streak {_consecutiveLosses}, entries today {_entriesToday}");
        }

        Persist();
    }

    public void InitialiseEquity(decimal equity)
    {
        SessionStartEquity = equity;
        PeakEquity = equity;
        Persist();
    }

    /// <summary>
    /// Stop opening new positions. Existing ones stay managed. The reason is
    /// only for the log: "by command" is the operator, anything else is the
    /// bot protecting itself (e.g. the venue refusing to close positions).
    /// </summary>
    public bool Pause(string reason = "by command")
    {
        if (EntriesPaused) return false;
        EntriesPaused = true;
        PausedAtUtc = DateTime.UtcNow;
        Log.Risk($"entries PAUSED {reason} - open positions stay managed");
        Persist();
        return true;
    }

    /// <summary>Allow new entries again.</summary>
    public bool Resume()
    {
        if (!EntriesPaused) return false;
        var since = PausedAtUtc is { } p ? DateTime.UtcNow - p : TimeSpan.Zero;
        EntriesPaused = false;
        PausedAtUtc = null;
        Log.Risk($"entries RESUMED by command after {since.TotalMinutes:F0}m");
        Persist();
        return true;
    }

    public void Halt(string reason, HaltKind kind = HaltKind.None)
    {
        if (Halted) return;
        Halted = true;
        HaltReason = reason;
        _haltKind = kind == HaltKind.None ? InferKind(reason) : kind;
        _haltedAtUtc = DateTime.UtcNow;
        Persist();
    }

    /// <summary>
    /// Account-level checks run every loop, before any per-trade logic.
    /// Returns true when the bot should flatten everything and stop.
    /// </summary>
    public bool CheckAccountHealth(decimal equity, IReadOnlyList<Position> positions, out string reason)
    {
        reason = "";
        TickUtcDay(equity);

        if (equity > PeakEquity)
        {
            PeakEquity = equity;
            Persist();
        }

        if (SessionStartEquity > 0)
        {
            var sessionDdPct = (SessionStartEquity - equity) / SessionStartEquity * 100m;
            if (sessionDdPct >= _cfg.MaxDailyDrawdownPct)
            {
                reason = $"session drawdown {sessionDdPct:F2}% reached the {_cfg.MaxDailyDrawdownPct}% limit " +
                         $"(equity {equity:F2} vs start {SessionStartEquity:F2})";
                Halt(reason, HaltKind.SessionDrawdown);
                return true;
            }

            if (_cfg.MaxDailyProfitPct > 0 && !ProfitLocked)
            {
                var profitPct = (equity - SessionStartEquity) / SessionStartEquity * 100m;
                if (profitPct >= _cfg.MaxDailyProfitPct)
                {
                    if (_cfg.ProfitLockRiskScale > 0)
                    {
                        // Keep trading, smaller. The peak-drawdown rule still
                        // catches a give-back of what has been earned.
                        _profitLockedDay = DateOnly.FromDateTime(DateTime.UtcNow);
                        Log.Risk($"daily profit lock hit {profitPct:F2}% (cap {_cfg.MaxDailyProfitPct}%) — " +
                                 $"continuing at {_cfg.ProfitLockRiskScale:P0} risk for the rest of the UTC day");
                        Persist();
                    }
                    else
                    {
                        reason = $"daily profit lock hit {profitPct:F2}% (cap {_cfg.MaxDailyProfitPct}%) — " +
                                 $"locking the green day at equity {equity:F2}";
                        Halt(reason, HaltKind.DailyProfit);
                        return true;
                    }
                }
            }
        }

        if (_cfg.MaxPeakDrawdownPct > 0 && PeakEquity > 0)
        {
            var peakDdPct = (PeakEquity - equity) / PeakEquity * 100m;
            if (peakDdPct >= _cfg.MaxPeakDrawdownPct)
            {
                reason = $"peak drawdown {peakDdPct:F2}% reached the {_cfg.MaxPeakDrawdownPct}% limit " +
                         $"(equity {equity:F2} vs peak {PeakEquity:F2})";
                Halt(reason, HaltKind.PeakDrawdown);
                return true;
            }
        }

        if (_cfg.FlattenOnMarginCall)
        {
            var bad = positions.FirstOrDefault(p => p.InTrouble);
            if (bad is not null)
            {
                reason = $"{bad.Market} reported liquidationState={bad.LiquidationState}";
                Halt(reason, HaltKind.MarginCall);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Books one closed trade into the loss controls.
    ///
    /// Every loss counts toward the streak, including those inside the
    /// FeeNoiseUsd band. Excluding them made the streak read 0 across 54
    /// consecutive losing trades: each was "just fees", yet together they were
    /// the whole drawdown. The band now only classifies - small losses are
    /// tracked separately for reporting - and decides what does NOT reset the
    /// streak: a fee-sized win proves nothing about the edge, so it leaves a
    /// losing run standing. A trade that netted exactly zero carries no
    /// information and is ignored.
    /// </summary>
    public void RecordTradeResult(bool profitable, decimal? netPnl = null)
    {
        if (netPnl == 0m) return;

        var feeSized = netPnl is { } n && Math.Abs(n) <= _cfg.FeeNoiseUsd;

        if (profitable)
        {
            if (feeSized) return;

            _consecutiveLosses = 0;
            _consecutiveSmallLosses = 0;
            _lossStreakUsd = 0m;
            Persist();
            return;
        }

        _consecutiveLosses++;
        if (feeSized) _consecutiveSmallLosses++;
        if (netPnl is { } loss && loss < 0m) _lossStreakUsd += -loss;

        if (_consecutiveLosses < _cfg.MaxConsecutiveLosses)
        {
            Persist();
            return;
        }

        if (_cfg.HaltOnConsecutiveLosses)
        {
            Halt($"{_consecutiveLosses} consecutive losses ({_consecutiveSmallLosses} fee-sized, " +
                 $"{_lossStreakUsd:0.00} USDT total) — hard session halt " +
                 $"(limit {_cfg.MaxConsecutiveLosses})", HaltKind.ConsecutiveLosses);
            return;
        }

        _cooldownUntil = DateTime.UtcNow.AddMinutes(_cfg.LossCooldownMinutes);
        _consecutiveLosses = 0;
        _consecutiveSmallLosses = 0;
        _lossStreakUsd = 0m;
        Persist();
    }

    public void RecordEntry(string market)
    {
        var now = DateTime.UtcNow;
        RollDayIfNeeded(now);
        _lastEntryPerMarket[market] = now;
        _entryTimestamps.Enqueue(now);
        _entriesToday++;
        Persist();
    }

    /// <summary>Gate checks that do not depend on sizing.</summary>
    public bool CanOpenNewPosition(
        string market,
        IReadOnlyList<Position> positions,
        out string reason,
        int reservedSlots = 0,
        IReadOnlyCollection<string>? reservedMarkets = null)
    {
        reason = "";
        var now = DateTime.UtcNow;
        RollDayIfNeeded(now);

        if (Halted) { reason = $"halted: {HaltReason}"; return false; }

        if (EntriesPaused) { reason = "paused (/resume to re-enable entries)"; return false; }

        if (!InsideEntryHours(_trading, now))
        {
            reason = $"outside entry hours {_trading.EntryHoursUtcStart:00}-{_trading.EntryHoursUtcEnd:00} UTC";
            return false;
        }

        if (_cooldownUntil is { } until && now < until)
        {
            reason = $"loss cooldown until {until:HH:mm:ss} UTC";
            return false;
        }

        var used = positions.Count + reservedSlots;
        if (used >= _cfg.MaxConcurrentPositions)
        {
            reason = $"{used} open/pending positions, limit {_cfg.MaxConcurrentPositions}";
            return false;
        }

        if (_cfg.OnePositionPerMarket &&
            (positions.Any(p => string.Equals(p.Market, market, StringComparison.OrdinalIgnoreCase)) ||
             (reservedMarkets is not null &&
              reservedMarkets.Any(m => string.Equals(m, market, StringComparison.OrdinalIgnoreCase)))))
        {
            reason = $"already holding {market}";
            return false;
        }

        if (_lastEntryPerMarket.TryGetValue(market, out var last))
        {
            var wait = TimeSpan.FromSeconds(_cfg.PerMarketCooldownSeconds) - (now - last);
            if (wait > TimeSpan.Zero)
            {
                reason = $"{market} cooldown, {wait.TotalSeconds:F0}s left";
                return false;
            }
        }

        var hourCutoff = now.AddHours(-1);
        while (_entryTimestamps.Count > 0 && _entryTimestamps.Peek() < hourCutoff)
            _entryTimestamps.Dequeue();
        if (_entryTimestamps.Count >= _cfg.MaxTradesPerHour)
        {
            reason = $"{_entryTimestamps.Count} entries in the last hour, limit {_cfg.MaxTradesPerHour}";
            return false;
        }

        if (_entriesToday >= _cfg.MaxTradesPerDay)
        {
            reason = $"{_entriesToday} entries today (UTC), limit {_cfg.MaxTradesPerDay}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Real round-trip cost for this market: the entry pays the maker fee when
    /// entries are post-only, the exit always pays taker because it is a market
    /// close. Falls back to the configured estimate only if the exchange reports
    /// no fees at all.
    /// </summary>
    public decimal RoundTripFeePct(MarketRules rules)
    {
        var entryFee = _trading.PostOnlyEntries ? rules.MakerFeePct : rules.TakerFeePct;
        var total = entryFee + rules.TakerFeePct;
        return total > 0 ? total : _cfg.RoundTripFeePct;
    }

    /// <summary>
    /// Position sizing from risk, not from a fixed notional: quantity is chosen
    /// so that being stopped out costs exactly RiskPerTradePct of equity.
    /// Then clamped by the notional ceilings and the market's own minimums.
    /// </summary>
    /// <param name="convictionScale">
    /// Extra scale from signal confidence in [ConvictionRiskFloor, 1]. Pass 1
    /// to size at full risk (subject to profit-lock / weekend scales).
    /// </param>
    public SizingResult Size(
        bool isLong,
        decimal entryPrice,
        decimal atr,
        decimal equity,
        decimal openNotional,
        MarketRules rules,
        decimal convictionScale = 1m,
        decimal entrySpreadPct = 0m)
    {
        if (entryPrice <= 0 || atr <= 0 || equity <= 0)
            return new SizingResult(false, 0, 0, 0, 0, "invalid inputs for sizing");

        var stopDistance = atr * _trading.StopLossAtrMult;
        var takeDistance = atr * _trading.TakeProfitAtrMult;
        if (stopDistance <= 0)
            return new SizingResult(false, 0, 0, 0, 0, "zero stop distance");

        // Reject setups whose target cannot pay for the round trip. The fee comes
        // from the market itself, not from a guess. A crossing entry also pays
        // the bid/ask spread, so its minimum target includes that cost rather
        // than treating a 12bp spread as free alpha.
        var roundTripFee = RoundTripFeePct(rules);
        var takePct = takeDistance / entryPrice * 100m;
        var stopPct = stopDistance / entryPrice * 100m;
        var spreadCost = Math.Max(0m, entrySpreadPct);
        var required = roundTripFee + spreadCost + _cfg.MinNetEdgePct;
        if (takePct < required)
            return new SizingResult(false, 0, 0, 0, 0,
                $"target {takePct:F3}% below cost-adjusted minimum {required:F3}% " +
                $"(round-trip fee {roundTripFee:F3}% + entry spread {spreadCost:F3}% + edge {_cfg.MinNetEdgePct:F3}%)");

        var minStop = Math.Max(_cfg.MinStopPct, roundTripFee * _cfg.MinStopFeeMultiple);
        if (stopPct < minStop)
            return new SizingResult(false, 0, 0, 0, 0,
                $"stop {stopPct:F3}% is inside the fee band (need {minStop:F3}%) - a winner still dies on the round trip");

        // Profit lock / weekend / conviction shrink both the risk and the caps,
        // so the reduced size is real and not clipped back up by a notional cap.
        var scale = RiskScale(convictionScale: convictionScale);
        var riskAmount = equity * (_cfg.RiskPerTradePct / 100m) * scale;
        var amount = riskAmount / stopDistance;

        // Notional ceilings.
        var maxByPosition = _cfg.MaxNotionalPerPositionUsd * scale / entryPrice;
        var remainingTotal = _cfg.MaxTotalNotionalUsd * scale - openNotional;
        if (remainingTotal <= 0)
            return new SizingResult(false, 0, 0, 0, 0,
                $"total open notional {openNotional:F2} already at the {_cfg.MaxTotalNotionalUsd} limit");
        var maxByTotal = remainingTotal / entryPrice;

        amount = Math.Min(amount, Math.Min(maxByPosition, maxByTotal));
        amount = rules.FloorAmount(amount);

        if (!rules.IsTradable(amount, entryPrice, out var why))
            return new SizingResult(false, 0, 0, 0, 0, why);

        var sign = isLong ? 1m : -1m;
        var stop = rules.SnapPriceForSide(entryPrice - sign * stopDistance, isBuy: !isLong);
        var take = rules.SnapPriceForSide(entryPrice + sign * takeDistance, isBuy: isLong);
        var hardStop = rules.SnapPriceForSide(
            entryPrice - sign * stopDistance * _trading.HardStopMultiplier, isBuy: !isLong);

        if (stop <= 0 || take <= 0 || hardStop <= 0)
            return new SizingResult(false, 0, 0, 0, 0, "protective prices snapped to zero or below");

        return new SizingResult(true, amount, stop, take, hardStop, "ok");
    }

    /// <summary>
    /// UTC day boundary: reset the daily entry counter, re-baseline session
    /// equity for the profit/drawdown locks, and clear day-scoped halts
    /// (daily profit, session/peak drawdown). Hard halts stay until
    /// ClearHaltOnBoot.
    /// </summary>
    /// <returns>True when a day-scoped halt was cleared and trading may resume.</returns>
    public bool TickUtcDay(decimal equity)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _entriesDay) return false;

        var previous = _entriesDay;
        _entriesDay = today;
        _entriesToday = 0;
        if (_profitLockedDay is not null)
        {
            Log.Risk("UTC day rolled — profit lock released, full risk restored");
            _profitLockedDay = null;
        }

        if (equity > 0)
        {
            SessionStartEquity = equity;
            PeakEquity = equity;
        }

        var cleared = false;
        if (Halted && HaltKindRules.ClearsOnUtcDay(_haltKind))
        {
            Log.Risk($"UTC day rolled ({previous} → {today}) — clearing day-scoped halt: {HaltReason}");
            Halted = false;
            HaltReason = "";
            _haltKind = HaltKind.None;
            _haltedAtUtc = null;
            cleared = true;
        }
        else
        {
            Log.Info($"UTC day rolled ({previous} → {today}) — session equity re-baselined at {equity:F2}");
        }

        Persist();
        return cleared;
    }

    private void RollDayIfNeeded(DateTime utcNow)
    {
        // Entry-gate path without a fresh equity reading: only roll the counter.
        // Full baseline reset happens in TickUtcDay / CheckAccountHealth.
        var today = DateOnly.FromDateTime(utcNow);
        if (today == _entriesDay) return;
        _entriesDay = today;
        _entriesToday = 0;
        Persist();
    }

    private static HaltKind InferKind(string reason)
    {
        if (reason.Contains("daily profit lock", StringComparison.OrdinalIgnoreCase))
            return HaltKind.DailyProfit;
        if (reason.Contains("session drawdown", StringComparison.OrdinalIgnoreCase))
            return HaltKind.SessionDrawdown;
        if (reason.Contains("peak drawdown", StringComparison.OrdinalIgnoreCase))
            return HaltKind.PeakDrawdown;
        if (reason.Contains("consecutive losses", StringComparison.OrdinalIgnoreCase))
            return HaltKind.ConsecutiveLosses;
        if (reason.Contains("liquidation", StringComparison.OrdinalIgnoreCase))
            return HaltKind.MarginCall;
        return HaltKind.None;
    }

    private void Persist()
    {
        _store.Save(new SessionState
        {
            ClearHaltOnBootConsumed = _clearHaltOnBootConsumed,
            Halted = Halted,
            HaltReason = HaltReason,
            HaltKind = _haltKind.ToString(),
            HaltedAtUtc = _haltedAtUtc,
            ConsecutiveLosses = _consecutiveLosses,
            ConsecutiveSmallLosses = _consecutiveSmallLosses,
            LossStreakUsd = _lossStreakUsd,
            EntriesToday = _entriesToday,
            EntriesDay = _entriesDay.ToString("yyyy-MM-dd"),
            SessionStartEquity = SessionStartEquity,
            PeakEquity = PeakEquity,
            CooldownUntilUtc = _cooldownUntil,
            ProfitLockedDay = _profitLockedDay?.ToString("yyyy-MM-dd") ?? "",
            EntriesPaused = EntriesPaused,
            PausedAtUtc = PausedAtUtc,
        });
    }
}
