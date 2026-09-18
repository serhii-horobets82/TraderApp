using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Signals;
using TraderApp.Trading;

namespace TraderApp.Backtest;

internal sealed class SimTrade : ITradeState
{
    public required string Market { get; init; }
    public bool IsLong { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal EntryAtr { get; init; }
    public DateTime OpenedUtc { get; init; }

    public decimal StopPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public decimal HighWater { get; set; }
    public bool MovedToBreakEven { get; set; }
    public decimal MaxFavourableR { get; set; }

    public decimal Notional { get; init; }
    public double Score { get; init; }
    public string Regime { get; init; } = "";

    public DateTime ClosedUtc { get; set; }
    public decimal ExitPrice { get; set; }
    public string ExitReason { get; set; } = "";
    public decimal MaxAdverseR { get; set; }
}

public sealed record BacktestResult(
    int Trades, int Wins, int Scratches, decimal NetUsd, decimal GrossUsd, decimal FeesUsd,
    decimal ExpectancyR, decimal ProfitFactor, decimal MaxDrawdownUsd,
    decimal AvgWinR, decimal AvgLossR, decimal AvgMfeR, decimal AvgMaeR,
    IReadOnlyDictionary<string, int> ExitReasons,
    IReadOnlyDictionary<string, (int n, decimal usd)> PerMarket)
{
    public decimal WinRate => Trades == 0 ? 0 : (decimal)Wins / Trades * 100m;

    /// <summary>
    /// Trades that ended within a fee of zero - break-even stop-outs. They are
    /// neither wins nor losses economically, and counting them either way is
    /// what made a backtest "win rate" of 62.5% sit next to 13.3% live for the
    /// same PnL.
    /// </summary>
    public decimal ScratchRate => Trades == 0 ? 0 : (decimal)Scratches / Trades * 100m;
}

/// <summary>
/// Bar-by-bar replay of the real <see cref="SignalEngine"/> and
/// <see cref="ExitRules"/> over historical candles.
///
/// What it models: entry on the next bar's open after a signal on a closed bar,
/// crossing the spread, both sides of the round-trip fee, intrabar stop and
/// target hits from bar highs and lows, and the full break-even / trail /
/// scratch / give-back / time-stop rule set.
///
/// What it cannot model, and therefore where it will flatter reality:
///  - no historical order book, so the `book` component always abstains and the
///    spread veto uses a fixed assumption rather than the real spread;
///  - stops and trails are evaluated at bar close, while the live bot checks
///    every few seconds, so intrabar stop placement differs;
///  - fills are assumed complete at the modelled price, with no queue or
///    partial-fill behaviour, and no funding.
/// Treat the output as a relative comparison between parameter sets, not as a
/// forecast of live PnL.
/// </summary>
public sealed class Backtester
{
    private readonly BotConfig _cfg;
    private readonly decimal _assumedSpreadBps;

    private readonly decimal _feeOverridePct;

    /// <param name="feeOverridePct">
    /// Round-trip fee to assume instead of the market's, for answering "would a
    /// cheaper fee tier make this viable?". 0 uses the real market fee.
    /// </param>
    public Backtester(BotConfig cfg, decimal assumedSpreadBps = 3m, decimal feeOverridePct = 0m)
    {
        _cfg = cfg;
        _assumedSpreadBps = assumedSpreadBps;
        _feeOverridePct = feeOverridePct;
    }

    public BacktestResult Run(
        IReadOnlyDictionary<string, (IReadOnlyList<Candle> entry, IReadOnlyList<Candle> regime)> data,
        IReadOnlyDictionary<string, MarketRules> rules)
    {
        var engine = new SignalEngine(_cfg.Signals);
        var closed = new List<SimTrade>();

        foreach (var (market, series) in data)
        {
            if (!rules.TryGetValue(market, out var mr)) continue;
            closed.AddRange(RunMarket(market, series.entry, series.regime, mr, engine));
        }

        return Summarise(closed);
    }

    private List<SimTrade> RunMarket(
        string market,
        IReadOnlyList<Candle> entry,
        IReadOnlyList<Candle> regime,
        MarketRules mr,
        SignalEngine engine)
    {
        var done = new List<SimTrade>();
        SimTrade? open = null;
        var regimeCursor = 0;

        var warmup = Math.Max(_cfg.Signals.EmaRegime, _cfg.Signals.AdxPeriod * 2 + 2) + 10;
        var atrSeries = Ta.Atr(entry, _cfg.Signals.AtrPeriod);
        var barLength = WhiteBitClient.ParseInterval(_cfg.Trading.EntryInterval);
        var roundTrip = _cfg.Trading.PostOnlyEntries
            ? mr.MakerFeePct + mr.TakerFeePct
            : mr.TakerFeePct * 2m;
        if (roundTrip <= 0) roundTrip = _cfg.Risk.RoundTripFeePct;
        if (_feeOverridePct > 0) roundTrip = _feeOverridePct;

        decimal Snap(decimal px, bool isBuy) => mr.SnapPriceForSide(px, isBuy);

        for (int i = warmup; i < entry.Count - 1; i++)
        {
            var bar = entry[i];
            var next = entry[i + 1];
            var barClose = bar.OpenTime + barLength;

            // ── Manage an open trade across this bar ────────────────────
            if (open is not null)
            {
                // Adverse/favourable excursion from the bar's extremes.
                var advPrice = open.IsLong ? bar.Low : bar.High;
                var favPrice = open.IsLong ? bar.High : bar.Low;
                var advR = ExitRules.RMultiple(open, advPrice, _cfg.Trading.StopLossAtrMult);
                var favR = ExitRules.RMultiple(open, favPrice, _cfg.Trading.StopLossAtrMult);
                if (advR < open.MaxAdverseR) open.MaxAdverseR = advR;

                var tpHit = open.IsLong ? bar.High >= open.TakeProfitPrice : bar.Low <= open.TakeProfitPrice;

                if (tpHit)
                {
                    // The target sits further out than any break-even or trailing
                    // stop, so reaching it means the favourable move happened.
                    Close(open, open.TakeProfitPrice, barClose, "target hit", done);
                    open = null;
                }
                else
                {
                    // Arm break-even and trailing from this bar's favourable
                    // extreme BEFORE testing the stop against its adverse extreme.
                    //
                    // The live bot re-evaluates every few seconds, so a trade that
                    // runs up, arms break-even and reverses inside one 15m bar is
                    // stopped out. Deferring the arm to the bar close and testing
                    // the stop only on the next bar missed every one of those, and
                    // that single omission is what produced a 46.6% backtest win
                    // rate against 13.3% live. Within-bar order is unknowable from
                    // OHLC; this is the reading that matches live behaviour.
                    ExitRules.UpdateStops(open, favPrice, _cfg.Trading, roundTrip, Snap);

                    var stopHit = open.IsLong ? bar.Low <= open.StopPrice : bar.High >= open.StopPrice;
                    if (stopHit)
                    {
                        Close(open, open.StopPrice, barClose, "stop hit", done);
                        open = null;
                    }
                    else
                    {
                        var reason = ExitRules.Decide(open, bar.Close, barClose, _cfg.Trading, roundTrip,
                            inTrouble: false, regimeAgainstUnderwater: false);
                        if (reason is not null)
                        {
                            Close(open, bar.Close, barClose, Categorise(reason), done);
                            open = null;
                        }
                    }
                }
            }

            if (open is not null) continue;

            // ── Look for an entry on this closed bar ────────────────────
            // Exactly what the live bot passes: the most recent CandleLookback
            // bars, not the whole history. Matching this matters for fidelity -
            // EMA/ADX seeds differ with window length - and it turns the replay
            // from O(n^2) into O(n).
            var lookback = Math.Max(_cfg.Trading.CandleLookback, 60);
            var entrySlice = new ListWindow<Candle>(entry, i + 2, lookback);

            // Advance a cursor instead of rescanning the regime series each bar.
            while (regimeCursor + 1 < regime.Count && regime[regimeCursor + 1].OpenTime <= bar.OpenTime)
                regimeCursor++;
            var regimeCount = regimeCursor + 1;
            if (regimeCount < _cfg.Signals.EmaRegime + 3) continue;
            var regimeSlice = new ListWindow<Candle>(regime, regimeCount, lookback);

            var book = SyntheticBook(bar.Close);
            var signal = engine.Evaluate(market, entrySlice, regimeSlice, book);
            if (!signal.IsActionable) continue;

            var atr = Ta.IsUsable(atrSeries[i]) ? (decimal)atrSeries[i] : 0m;
            if (atr <= 0) continue;

            var isLong = signal.Direction == Direction.Long;
            var half = next.Open * (_assumedSpreadBps / 2m / 10_000m);
            var fill = Snap(isLong ? next.Open + half : next.Open - half, isLong);
            if (fill <= 0) continue;

            var stopDist = atr * _cfg.Trading.StopLossAtrMult;
            var takeDist = atr * _cfg.Trading.TakeProfitAtrMult;

            // Same fee-edge gate the live sizer applies.
            if (takeDist / fill * 100m < roundTrip + _cfg.Risk.MinNetEdgePct) continue;

            var sign = isLong ? 1m : -1m;
            open = new SimTrade
            {
                Market = market,
                IsLong = isLong,
                EntryPrice = fill,
                EntryAtr = atr,
                OpenedUtc = next.OpenTime,
                Notional = _cfg.Risk.MaxNotionalPerPositionUsd,
                Score = signal.Score,
                Regime = signal.Regime,
                StopPrice = Snap(fill - sign * stopDist, !isLong),
                TakeProfitPrice = Snap(fill + sign * takeDist, isLong),
                HighWater = fill
            };
        }

        return done;
    }

    private static void Close(SimTrade t, decimal price, DateTime whenUtc, string reason, List<SimTrade> into)
    {
        t.ExitPrice = price;
        t.ClosedUtc = whenUtc;
        t.ExitReason = reason;
        into.Add(t);
    }

    private static string Categorise(string reason) =>
        reason.StartsWith("never worked") ? "scratch"
        : reason.StartsWith("gave back") ? "give-back"
        : reason.StartsWith("time stop") ? "time stop"
        : reason.StartsWith("stop hit") ? "stop hit"
        : reason.StartsWith("target hit") ? "target hit"
        : reason;

    /// <summary>
    /// A book with a symmetric spread and equal depth, so the `book` component
    /// abstains rather than inventing an imbalance that never existed.
    /// </summary>
    private OrderBook SyntheticBook(decimal mid)
    {
        var half = mid * (_assumedSpreadBps / 2m / 10_000m);
        return new OrderBook
        {
            Bids = new List<List<decimal>> { new() { mid - half, 1m } },
            Asks = new List<List<decimal>> { new() { mid + half, 1m } }
        };
    }

    private BacktestResult Summarise(List<SimTrade> trades)
    {
        if (trades.Count == 0)
            return new BacktestResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, int>(), new Dictionary<string, (int, decimal)>());

        decimal gross = 0, fees = 0, grossWin = 0, grossLoss = 0;
        decimal sumR = 0, sumWinR = 0, sumLossR = 0, sumMfe = 0, sumMae = 0;
        int wins = 0, scratches = 0;
        var equity = new List<decimal>();
        decimal running = 0, peak = 0, maxDd = 0;
        var reasons = new Dictionary<string, int>();
        var perMarket = new Dictionary<string, (int n, decimal usd)>();

        foreach (var t in trades.OrderBy(t => t.ClosedUtc))
        {
            var sign = t.IsLong ? 1m : -1m;
            var movePct = (t.ExitPrice - t.EntryPrice) * sign / t.EntryPrice * 100m;
            var g = t.Notional * movePct / 100m;
            var feePct = _feeOverridePct > 0 ? _feeOverridePct : _cfg.Risk.RoundTripFeePct;
            var f = t.Notional * feePct / 100m;
            var net = g - f;

            gross += g;
            fees += f;
            running += net;
            peak = Math.Max(peak, running);
            maxDd = Math.Max(maxDd, peak - running);
            equity.Add(running);

            var r = ExitRules.RMultiple(t, t.ExitPrice, _cfg.Trading.StopLossAtrMult);
            sumR += r;
            sumMfe += t.MaxFavourableR;
            sumMae += t.MaxAdverseR;

            if (Math.Abs(net) < _cfg.Risk.FeeNoiseUsd) scratches++;
            if (net > 0) { wins++; grossWin += net; sumWinR += r; }
            else { grossLoss += -net; sumLossR += r; }

            reasons[t.ExitReason] = reasons.GetValueOrDefault(t.ExitReason) + 1;
            var pm = perMarket.GetValueOrDefault(t.Market);
            perMarket[t.Market] = (pm.n + 1, pm.usd + net);
        }

        int losses = trades.Count - wins;
        return new BacktestResult(
            trades.Count, wins, scratches, gross - fees, gross, fees,
            sumR / trades.Count,
            grossLoss > 0 ? grossWin / grossLoss : 0,
            maxDd,
            wins > 0 ? sumWinR / wins : 0,
            losses > 0 ? sumLossR / losses : 0,
            sumMfe / trades.Count,
            sumMae / trades.Count,
            reasons, perMarket);
    }
}
