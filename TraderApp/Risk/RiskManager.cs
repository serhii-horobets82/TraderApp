using TraderApp.Configuration;
using TraderApp.Exchange;

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

    private readonly Dictionary<string, DateTime> _lastEntryPerMarket = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> _entryTimestamps = new();

    private int _consecutiveLosses;
    private int _entriesToday;
    private DateOnly _entriesDay;
    private DateTime? _cooldownUntil;

    public decimal SessionStartEquity { get; private set; }
    public decimal PeakEquity { get; private set; }
    public int ConsecutiveLosses => _consecutiveLosses;
    public int EntriesToday => _entriesToday;
    public bool Halted { get; private set; }
    public string HaltReason { get; private set; } = "";

    public RiskManager(RiskConfig cfg, TradingConfig trading)
    {
        _cfg = cfg;
        _trading = trading;
        _entriesDay = DateOnly.FromDateTime(DateTime.UtcNow);
    }

    public void InitialiseEquity(decimal equity)
    {
        SessionStartEquity = equity;
        PeakEquity = equity;
    }

    public void Halt(string reason)
    {
        if (Halted) return;
        Halted = true;
        HaltReason = reason;
    }

    /// <summary>
    /// Account-level checks run every loop, before any per-trade logic.
    /// Returns true when the bot should flatten everything and stop.
    /// </summary>
    public bool CheckAccountHealth(decimal equity, IReadOnlyList<Position> positions, out string reason)
    {
        reason = "";
        if (equity > PeakEquity) PeakEquity = equity;

        if (SessionStartEquity > 0)
        {
            var sessionDdPct = (SessionStartEquity - equity) / SessionStartEquity * 100m;
            if (sessionDdPct >= _cfg.MaxDailyDrawdownPct)
            {
                reason = $"session drawdown {sessionDdPct:F2}% reached the {_cfg.MaxDailyDrawdownPct}% limit " +
                         $"(equity {equity:F2} vs start {SessionStartEquity:F2})";
                Halt(reason);
                return true;
            }

            if (_cfg.MaxDailyProfitPct > 0)
            {
                var profitPct = (equity - SessionStartEquity) / SessionStartEquity * 100m;
                if (profitPct >= _cfg.MaxDailyProfitPct)
                {
                    reason = $"daily profit lock hit {profitPct:F2}% (cap {_cfg.MaxDailyProfitPct}%) — " +
                             $"locking the green day at equity {equity:F2}";
                    Halt(reason);
                    return true;
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
                Halt(reason);
                return true;
            }
        }

        if (_cfg.FlattenOnMarginCall)
        {
            var bad = positions.FirstOrDefault(p => p.InTrouble);
            if (bad is not null)
            {
                reason = $"{bad.Market} reported liquidationState={bad.LiquidationState}";
                Halt(reason);
                return true;
            }
        }

        return false;
    }

    public void RecordTradeResult(bool profitable, decimal? netPnl = null)
    {
        if (netPnl is { } n && Math.Abs(n) <= _cfg.FeeNoiseUsd)
            return;

        if (profitable)
        {
            _consecutiveLosses = 0;
            return;
        }

        _consecutiveLosses++;
        if (_consecutiveLosses < _cfg.MaxConsecutiveLosses)
            return;

        if (_cfg.HaltOnConsecutiveLosses)
        {
            Halt($"{_consecutiveLosses} consecutive losses — hard session halt " +
                 $"(limit {_cfg.MaxConsecutiveLosses})");
            return;
        }

        _cooldownUntil = DateTime.UtcNow.AddMinutes(_cfg.LossCooldownMinutes);
        _consecutiveLosses = 0;
    }

    public void RecordEntry(string market)
    {
        var now = DateTime.UtcNow;
        RollDayIfNeeded(now);
        _lastEntryPerMarket[market] = now;
        _entryTimestamps.Enqueue(now);
        _entriesToday++;
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
    public SizingResult Size(
        bool isLong,
        decimal entryPrice,
        decimal atr,
        decimal equity,
        decimal openNotional,
        MarketRules rules)
    {
        if (entryPrice <= 0 || atr <= 0 || equity <= 0)
            return new SizingResult(false, 0, 0, 0, 0, "invalid inputs for sizing");

        var stopDistance = atr * _trading.StopLossAtrMult;
        var takeDistance = atr * _trading.TakeProfitAtrMult;
        if (stopDistance <= 0)
            return new SizingResult(false, 0, 0, 0, 0, "zero stop distance");

        // Reject setups whose target cannot pay for the round trip. The fee comes
        // from the market itself, not from a guess: on the majors WhiteBIT quotes
        // 0.1% maker and 0.1% taker, so a 0.2% round trip - which is more than a
        // naive "close at +0.4% PnL" scalp nets in the first place.
        var roundTripFee = RoundTripFeePct(rules);
        var takePct = takeDistance / entryPrice * 100m;
        var stopPct = stopDistance / entryPrice * 100m;
        var required = roundTripFee + _cfg.MinNetEdgePct;
        if (takePct < required)
            return new SizingResult(false, 0, 0, 0, 0,
                $"target {takePct:F3}% below fee-adjusted minimum {required:F3}% " +
                $"(round-trip fee {roundTripFee:F3}% + edge {_cfg.MinNetEdgePct:F3}%)");

        var minStop = Math.Max(_cfg.MinStopPct, roundTripFee * _cfg.MinStopFeeMultiple);
        if (stopPct < minStop)
            return new SizingResult(false, 0, 0, 0, 0,
                $"stop {stopPct:F3}% is inside the fee band (need {minStop:F3}%) - a winner still dies on the round trip");

        var riskAmount = equity * (_cfg.RiskPerTradePct / 100m);
        var amount = riskAmount / stopDistance;

        // Notional ceilings.
        var maxByPosition = _cfg.MaxNotionalPerPositionUsd / entryPrice;
        var remainingTotal = _cfg.MaxTotalNotionalUsd - openNotional;
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

    private void RollDayIfNeeded(DateTime utcNow)
    {
        var today = DateOnly.FromDateTime(utcNow);
        if (today == _entriesDay) return;
        _entriesDay = today;
        _entriesToday = 0;
    }
}
