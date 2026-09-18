using TraderApp.Configuration;

namespace TraderApp.Trading;

/// <summary>
/// The mutable per-trade state the stop and exit rules operate on.
/// Implemented by both the live <see cref="ManagedPosition"/> and the
/// backtester's simulated trade, so the two cannot drift apart.
/// </summary>
public interface ITradeState
{
    bool IsLong { get; }
    decimal EntryPrice { get; }
    decimal EntryAtr { get; }
    DateTime OpenedUtc { get; }

    decimal StopPrice { get; set; }
    decimal TakeProfitPrice { get; set; }
    decimal HighWater { get; set; }
    bool MovedToBreakEven { get; set; }
    decimal MaxFavourableR { get; set; }
}

/// <summary>
/// Pure stop-management and exit logic, shared by the live trader and the
/// backtester.
///
/// This exists because a backtest that reimplements the rules measures the
/// reimplementation, not the strategy. Any divergence between the two is a
/// silent lie about expected performance, so there is exactly one copy.
/// </summary>
public static class ExitRules
{
    /// <summary>Profit in multiples of the initial stop distance.</summary>
    public static decimal RMultiple(ITradeState m, decimal mark, decimal stopLossAtrMult)
    {
        var r = m.EntryAtr * stopLossAtrMult;
        if (r <= 0) return 0m;
        var sign = m.IsLong ? 1m : -1m;
        return (mark - m.EntryPrice) * sign / r;
    }

    /// <summary>
    /// Records the running high-water mark and best excursion, then moves the
    /// stop to break-even and finally trails it. Returns a description of any
    /// stop change, or null.
    /// </summary>
    public static string? UpdateStops(
        ITradeState m,
        decimal mark,
        TradingConfig cfg,
        decimal roundTripFeePct,
        Func<decimal, bool, decimal> snapPrice)
    {
        var sign = m.IsLong ? 1m : -1m;
        var rMultiple = RMultiple(m, mark, cfg.StopLossAtrMult);

        if (m.IsLong) m.HighWater = Math.Max(m.HighWater, mark);
        else m.HighWater = m.HighWater == 0 ? mark : Math.Min(m.HighWater, mark);

        if (rMultiple > m.MaxFavourableR) m.MaxFavourableR = rMultiple;

        string? change = null;

        // Break-even. The stop sits at entry + the round-trip fee, which in R
        // terms is feeCushionR above entry - so the trigger must clear that or
        // the stop would land above the mark and close the trade instantly.
        var stopDistance = m.EntryAtr * cfg.StopLossAtrMult;
        var feeCushion = m.EntryPrice * (roundTripFeePct / 100m);
        var feeCushionR = stopDistance > 0 ? feeCushion / stopDistance : 0m;
        var breakEvenTrigger = Math.Max(cfg.BreakEvenAtR, feeCushionR + cfg.BreakEvenBufferR);

        if (cfg.BreakEvenEnabled && !m.MovedToBreakEven && breakEvenTrigger > 0 && rMultiple >= breakEvenTrigger)
        {
            var be = snapPrice(m.EntryPrice + sign * feeCushion, !m.IsLong);
            var improves = (m.IsLong && be > m.StopPrice) || (!m.IsLong && be < m.StopPrice);
            var behindMark = (m.IsLong && be < mark) || (!m.IsLong && be > mark);
            if (improves && behindMark)
            {
                m.StopPrice = be;
                m.MovedToBreakEven = true;
                change = $"break-even+fees {be} at {rMultiple:F2}R (trigger {breakEvenTrigger:F2}R)";
            }
        }

        if (cfg.TrailStartAtR > 0 && rMultiple >= cfg.TrailStartAtR)
        {
            var trail = snapPrice(m.HighWater - sign * m.EntryAtr * cfg.TrailAtrMult, !m.IsLong);
            if ((m.IsLong && trail > m.StopPrice) || (!m.IsLong && trail < m.StopPrice))
            {
                m.StopPrice = trail;
                change = $"trailing stop -> {trail} ({rMultiple:F2}R)";
            }
        }

        return change;
    }

    /// <summary>
    /// Decides whether the trade should close now. Returns the reason, or null
    /// to hold. Conditions are in priority order; the stop always wins.
    /// </summary>
    public static string? Decide(
        ITradeState m,
        decimal mark,
        DateTime nowUtc,
        TradingConfig cfg,
        decimal roundTripFeePct,
        bool inTrouble,
        bool regimeAgainstUnderwater)
    {
        var rMultiple = RMultiple(m, mark, cfg.StopLossAtrMult);
        var sign = m.IsLong ? 1m : -1m;
        var moveInFavour = (mark - m.EntryPrice) * sign;
        var age = nowUtc - m.OpenedUtc;

        if (m.IsLong && mark <= m.StopPrice) return $"stop hit ({mark} <= {m.StopPrice})";
        if (!m.IsLong && mark >= m.StopPrice) return $"stop hit ({mark} >= {m.StopPrice})";
        if (m.IsLong && mark >= m.TakeProfitPrice) return $"target hit ({mark} >= {m.TakeProfitPrice})";
        if (!m.IsLong && mark <= m.TakeProfitPrice) return $"target hit ({mark} <= {m.TakeProfitPrice})";
        if (inTrouble) return "margin call / liquidation state";

        if (cfg.MaxPositionMinutes > 0 && age > TimeSpan.FromMinutes(cfg.MaxPositionMinutes))
            return $"time stop after {cfg.MaxPositionMinutes} minutes";

        // Only scratch a trade that never went anywhere - see the ten
        // consecutive fee-losses this rule used to manufacture.
        if (cfg.ScratchUnprofitableMinutes > 0 &&
            rMultiple < 0 &&
            m.MaxFavourableR < cfg.ScratchMaxFavourableR &&
            age > TimeSpan.FromMinutes(cfg.ScratchUnprofitableMinutes))
            return $"never worked - {rMultiple:F2}R now, best {m.MaxFavourableR:F2}R";

        if (cfg.GiveBackArmR > 0 &&
            m.MaxFavourableR >= cfg.GiveBackArmR &&
            rMultiple > 0 &&
            m.MaxFavourableR - rMultiple >= cfg.GiveBackDropR &&
            m.EntryPrice > 0 &&
            moveInFavour / m.EntryPrice * 100m >= roundTripFeePct * 1.3m)
            return $"gave back peak {m.MaxFavourableR:F2}R to {rMultiple:F2}R";

        if (regimeAgainstUnderwater)
            return "position fights the higher-timeframe regime while underwater";

        return null;
    }
}
