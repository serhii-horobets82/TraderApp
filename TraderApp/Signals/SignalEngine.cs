using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;

namespace TraderApp.Signals;

public enum Direction { None, Long, Short }

public sealed record SignalComponent(string Name, Direction Direction, double Weight, string Detail)
{
    /// <summary>Event-like votes. Regime/EMA/VWAP are background state and can stay true for hours.</summary>
    public bool IsTrigger => Name is "rsi" or "bollinger" or "volume" or "book" or "macd" or "stoch" or "structure";
}

public sealed record TradeSignal
{
    public required string Market { get; init; }
    public required Direction Direction { get; init; }
    public required double Score { get; init; }
    public required decimal Price { get; init; }
    public required decimal Atr { get; init; }
    public required decimal AtrPct { get; init; }
    public required decimal SpreadBps { get; init; }
    public required IReadOnlyList<SignalComponent> Components { get; init; }
    public required IReadOnlyList<string> Vetoes { get; init; }
    public string Regime { get; init; } = "";
    public Direction RegimeDirection { get; init; }

    public bool IsActionable => Direction != Direction.None && Vetoes.Count == 0;

    /// <summary>
    /// Spread and book can clear on the next tick; candle vetoes cannot.
    /// Used to retry a setup mid-bar without re-logging a dead 15m signal.
    /// </summary>
    public bool OnlyMicrostructureVetoes =>
        Vetoes.Count > 0 && Vetoes.All(v =>
            v.StartsWith("spread ", StringComparison.Ordinal) ||
            v.StartsWith("book ", StringComparison.Ordinal) ||
            v.StartsWith("depth ", StringComparison.Ordinal) ||
            v.Equals("empty order book side", StringComparison.Ordinal));

    public string Explain()
    {
        var agree = Components.Where(c => c.Direction == Direction).Select(c => c.Name);
        var parts = string.Join(", ", agree);
        return Vetoes.Count > 0
            ? $"blocked [{string.Join("; ", Vetoes)}]"
            : $"{Direction} score={Score:F2} regime={Regime} via [{parts}]";
    }
}

/// <summary>
/// Turns candles plus the order book into a direction and a confidence score.
///
/// The design is deliberately confluence-based rather than a single crossover:
/// each component votes independently and a trade only happens when enough of
/// them agree AND no veto filter fires. Vetoes are absolute - they are the part
/// that keeps the strategy out of the conditions where scalping loses money
/// (wide spreads, dead volatility, news-driven spikes).
/// </summary>
public sealed class SignalEngine
{
    private readonly SignalConfig _cfg;

    public SignalEngine(SignalConfig cfg) => _cfg = cfg;

    public TradeSignal Evaluate(
        string market,
        IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> regimeCandles,
        OrderBook book,
        StandAsideSnapshot? standAside = null)
    {
        var vetoes = new List<string>();
        var components = new List<SignalComponent>();

        var price = book.Mid ?? (entryCandles.Count > 0 ? entryCandles[^1].Close : 0m);

        // Indicators read the last CLOSED bar. Scoring the still-forming candle
        // made SOL flicker 0.58 → 0.48 between ticks and missed one-bar triggers.
        var need = Math.Max(_cfg.EmaRegime,
                       Math.Max(_cfg.AdxPeriod * 2 + 2,
                       Math.Max(_cfg.BollingerPeriod,
                       Math.Max(_cfg.MacdSlow + _cfg.MacdSignal, _cfg.DonchianPeriod)))) + 6;
        if (entryCandles.Count < need)
            return Blocked(market, price, $"not enough entry candles ({entryCandles.Count}/{need})");
        if (regimeCandles.Count < _cfg.EmaRegime + 3)
            return Blocked(market, price, $"not enough regime candles ({regimeCandles.Count}/{_cfg.EmaRegime + 3})");
        if (price <= 0)
            return Blocked(market, price, "no usable price");

        var closes = Ta.Closes(entryCandles);
        var volumes = Ta.Volumes(entryCandles);

        var emaFast = Ta.Ema(closes, _cfg.EmaFast);
        var emaSlow = Ta.Ema(closes, _cfg.EmaSlow);
        var rsi = Ta.Rsi(closes, _cfg.RsiPeriod);
        var atr = Ta.Atr(entryCandles, _cfg.AtrPeriod);
        var adx = Ta.Adx(entryCandles, _cfg.AdxPeriod);
        var bb = Ta.Bollinger(closes, _cfg.BollingerPeriod, _cfg.BollingerStdDev);
        var volMa = Ta.Sma(volumes, _cfg.VolumeMaPeriod);
        var vwap = Ta.RollingVwap(entryCandles, _cfg.BollingerPeriod);
        var macd = Ta.Macd(closes, _cfg.MacdFast, _cfg.MacdSlow, _cfg.MacdSignal);
        var stoch = Ta.Stochastic(entryCandles, _cfg.StochPeriod, _cfg.StochSmoothK, _cfg.StochSmoothD);

        int i = entryCandles.Count - 2;
        var atrNow = Ta.IsUsable(atr[i]) ? (decimal)atr[i] : 0m;
        var atrPct = price > 0 ? atrNow / price * 100m : 0m;
        var spreadBps = book.SpreadBps ?? decimal.MaxValue;

        // ── Veto filters ────────────────────────────────────────────────
        if (spreadBps > _cfg.MaxSpreadBps)
            vetoes.Add($"spread {spreadBps:F2}bps > {_cfg.MaxSpreadBps}bps");
        if (atrNow <= 0)
            vetoes.Add("ATR unavailable");
        else if (atrPct < _cfg.MinAtrPct)
            vetoes.Add($"ATR {atrPct:F3}% < {_cfg.MinAtrPct}% (too quiet, fees would eat the move)");
        else if (atrPct > _cfg.MaxAtrPct)
            vetoes.Add($"ATR {atrPct:F3}% > {_cfg.MaxAtrPct}% (volatility spike)");
        if (book.BestBid is null || book.BestAsk is null)
            vetoes.Add("empty order book side");
        var thinSide = book.ThinSideDepthQuote(_cfg.OrderBookDepth);
        if (_cfg.MinBookDepthUsd > 0 && thinSide < _cfg.MinBookDepthUsd)
            vetoes.Add($"depth {thinSide:F0} USDT on the thin side < {_cfg.MinBookDepthUsd:F0} (top {_cfg.OrderBookDepth} levels)");

        // ── Regime filter on the higher timeframe ───────────────────────
        var regimeCloses = Ta.Closes(regimeCandles);
        var regimeEma = Ta.Ema(regimeCloses, _cfg.EmaRegime);
        int ri = regimeCandles.Count - 2;
        var regimeClosed = regimeEma.Take(ri + 1).Where(Ta.IsUsable).ToArray();
        var regimeSlope = Ta.NormalisedSlope(regimeClosed, 3);
        var regimePrice = regimeCloses[ri];
        var regimeEmaNow = regimeEma[ri];

        Direction regime = Direction.None;
        string regimeLabel = "flat";
        if (Ta.IsUsable(regimeEmaNow) && Ta.IsUsable(regimeSlope))
        {
            if (regimePrice > regimeEmaNow && regimeSlope > 0) { regime = Direction.Long; regimeLabel = "up"; }
            else if (regimePrice < regimeEmaNow && regimeSlope < 0) { regime = Direction.Short; regimeLabel = "down"; }
        }

        var adxNow = Ta.IsUsable(adx[i]) ? adx[i] : 0;
        var trending = adxNow >= _cfg.AdxTrendThreshold;
        double rsiNow = Ta.IsUsable(rsi[i]) ? rsi[i] : 50;
        double volRatio = Ta.IsUsable(volMa[i]) && volMa[i] > 0 ? volumes[i] / volMa[i] : 0;

        // ── Components ──────────────────────────────────────────────────

        // 1. Higher-timeframe regime. Highest weight: trading against it is how
        //    scalpers get run over.
        components.Add(new SignalComponent("regime", regime, 0.28,
            $"{regimeLabel} (ADX {adxNow:F1}, {(trending ? "trending" : "chop")})"));

        // 2. Fast/slow EMA relationship on the entry timeframe.
        if (Ta.IsUsable(emaFast[i]) && Ta.IsUsable(emaSlow[i]))
        {
            var dir = emaFast[i] > emaSlow[i] ? Direction.Long : Direction.Short;
            var sep = Math.Abs(emaFast[i] - emaSlow[i]) / Math.Max(1e-9, emaSlow[i]);
            // Weight scales with separation so a flat, entangled pair barely votes.
            var w = Math.Clamp(sep / 0.001, 0, 1) * 0.18;
            components.Add(new SignalComponent("ema", dir, w,
                $"EMA{_cfg.EmaFast} {(dir == Direction.Long ? ">" : "<")} EMA{_cfg.EmaSlow} (sep {sep * 100:F3}%)"));
        }

        // 3. RSI. Lookback of 2 closed bars - a 3-bar window was letting
        //    45-minute-old crosses count as a fresh entry.
        if (Ta.IsUsable(rsi[i]) && Ta.IsUsable(rsi[i - 1]))
        {
            double r = rsi[i];
            Direction dir = Direction.None;
            string detail = $"RSI {r:F1}";

            if (trending)
            {
                if (regime == Direction.Long && CrossedUp(rsi, i, 50, 2))
                { dir = Direction.Long; detail += " pullback reclaimed 50"; }
                else if (regime == Direction.Short && CrossedDown(rsi, i, 50, 2))
                { dir = Direction.Short; detail += " bounce rejected at 50"; }
            }
            else
            {
                if (CrossedUp(rsi, i, _cfg.RsiOversold, 2))
                { dir = Direction.Long; detail += " exiting oversold"; }
                else if (CrossedDown(rsi, i, _cfg.RsiOverbought, 2))
                { dir = Direction.Short; detail += " exiting overbought"; }
            }

            components.Add(new SignalComponent("rsi", dir, 0.20, detail));

            if (r >= _cfg.RsiExtremeHigh) vetoes.Add($"RSI {r:F1} extreme high - no long, no short into a squeeze");
            if (r <= _cfg.RsiExtremeLow) vetoes.Add($"RSI {r:F1} extreme low - no short, no long into a flush");
        }

        // 4. Bollinger / VWAP mean reversion, only meaningful in chop.
        if (Ta.IsUsable(bb.Lower[i]) && Ta.IsUsable(bb.Upper[i]) && !trending)
        {
            Direction dir = Direction.None;
            string detail = "inside bands";
            if (ReclaimedBand(closes, bb.Lower, i, fromBelow: true, lookback: 2))
            { dir = Direction.Long; detail = "closed back inside lower band"; }
            else if (ReclaimedBand(closes, bb.Upper, i, fromBelow: false, lookback: 2))
            { dir = Direction.Short; detail = "closed back inside upper band"; }
            components.Add(new SignalComponent("bollinger", dir, 0.14, detail));
        }
        else if (Ta.IsUsable(vwap[i]) && trending)
        {
            var dir = closes[i] > vwap[i] ? Direction.Long : Direction.Short;
            components.Add(new SignalComponent("vwap", dir, 0.14,
                $"price {(dir == Direction.Long ? "above" : "below")} rolling VWAP"));
        }

        // 5. Volume confirmation - a move on no volume is noise.
        if (Ta.IsUsable(volMa[i]) && volMa[i] > 0)
        {
            var ratio = volumes[i] / volMa[i];
            var prevRatio = Ta.IsUsable(volMa[i - 1]) && volMa[i - 1] > 0 ? volumes[i - 1] / volMa[i - 1] : 0;
            var best = Math.Max(ratio, prevRatio);
            var body = closes[i] - (double)entryCandles[i].Open;
            var dir = best >= 1.05 ? (body >= 0 ? Direction.Long : Direction.Short) : Direction.None;
            components.Add(new SignalComponent("volume", dir, 0.10, $"vol {ratio:F2}x MA{_cfg.VolumeMaPeriod}"));
        }

        // 6. Order-book imbalance - the only component that sees resting
        //    liquidity rather than history.
        var imb = book.Imbalance(_cfg.OrderBookDepth);
        var imbDir = imb > _cfg.ImbalanceThreshold ? Direction.Long
            : imb < -_cfg.ImbalanceThreshold ? Direction.Short
            : Direction.None;
        components.Add(new SignalComponent("book", imbDir, 0.10, $"imbalance {imb:+0.00;-0.00}"));

        // 7. MACD histogram flip - momentum, not the same as EMA9/21.
        if (i > 0 && Ta.IsUsable(macd.Histogram[i]) && Ta.IsUsable(macd.Histogram[i - 1]))
        {
            Direction dir = Direction.None;
            var h = macd.Histogram[i];
            var hp = macd.Histogram[i - 1];
            string detail = $"hist {h:+0.0000;-0.0000}";
            if (hp <= 0 && h > 0) { dir = Direction.Long; detail += " crossed above 0"; }
            else if (hp >= 0 && h < 0) { dir = Direction.Short; detail += " crossed below 0"; }
            components.Add(new SignalComponent("macd", dir, 0.12, detail));
        }

        // 8. Slow stochastic. Independent of RSI: it uses the bar's range, not
        //    close-to-close gains. Only votes on a cross out of the band.
        if (i > 0 && Ta.IsUsable(stoch.K[i]) && Ta.IsUsable(stoch.D[i]) &&
            Ta.IsUsable(stoch.K[i - 1]) && Ta.IsUsable(stoch.D[i - 1]))
        {
            Direction dir = Direction.None;
            string detail = $"%K {stoch.K[i]:F1} / %D {stoch.D[i]:F1}";
            var crossedUp = stoch.K[i - 1] <= stoch.D[i - 1] && stoch.K[i] > stoch.D[i];
            var crossedDown = stoch.K[i - 1] >= stoch.D[i - 1] && stoch.K[i] < stoch.D[i];
            if (crossedUp && stoch.K[i] <= _cfg.StochOversold + 15)
            { dir = Direction.Long; detail += " lift from oversold"; }
            else if (crossedDown && stoch.K[i] >= _cfg.StochOverbought - 15)
            { dir = Direction.Short; detail += " roll from overbought"; }
            components.Add(new SignalComponent("stoch", dir, 0.12, detail));
        }

        // 9. Price structure. Trend: Donchian break. Chop: engulfing bar.
        {
            Direction dir = Direction.None;
            string detail = "no structure";
            if (trending)
            {
                var hh = Ta.PriorHighestHigh(entryCandles, i, _cfg.DonchianPeriod);
                var ll = Ta.PriorLowestLow(entryCandles, i, _cfg.DonchianPeriod);
                // Breakouts are the low-win-rate side. Prefer a close back
                // through the prior extreme after a tag, not a fresh chase.
                if (Ta.IsUsable(hh) && closes[i] > hh && closes[i - 1] <= hh && rsiNow < _cfg.MaxTrendRsiLong - 4)
                { dir = Direction.Long; detail = $"Donchian{_cfg.DonchianPeriod} high, RSI not extended"; }
                else if (Ta.IsUsable(ll) && closes[i] < ll && closes[i - 1] >= ll && rsiNow > _cfg.MinTrendRsiShort + 4)
                { dir = Direction.Short; detail = $"Donchian{_cfg.DonchianPeriod} low, RSI not extended"; }
            }
            else if (i > 0)
            {
                var prev = entryCandles[i - 1];
                var curr = entryCandles[i];
                var prevBody = Math.Abs(prev.Close - prev.Open);
                var currBody = Math.Abs(curr.Close - curr.Open);
                var bull = curr.Close > curr.Open && prev.Close < prev.Open &&
                           curr.Close >= prev.Open && curr.Open <= prev.Close && currBody > prevBody;
                var bear = curr.Close < curr.Open && prev.Close > prev.Open &&
                           curr.Close <= prev.Open && curr.Open >= prev.Close && currBody > prevBody;
                if (bull) { dir = Direction.Long; detail = "bullish engulfing"; }
                else if (bear) { dir = Direction.Short; detail = "bearish engulfing"; }
            }
            components.Add(new SignalComponent("structure", dir, 0.10, detail));
        }

        // ── Aggregate ───────────────────────────────────────────────────
        // Neutral votes used to sit in the denominator and silently fail a
        // 0.60 setup against a 0.62 threshold (regime+EMA+VWAP). Only sides
        // that actually voted count.
        double longScore = components.Where(c => c.Direction == Direction.Long).Sum(c => c.Weight);
        double shortScore = components.Where(c => c.Direction == Direction.Short).Sum(c => c.Weight);
        double votedWeight = longScore + shortScore;
        if (votedWeight <= 0) votedWeight = 1;

        Direction chosen;
        double score;
        if (longScore > shortScore) { chosen = Direction.Long; score = longScore / votedWeight; }
        else if (shortScore > longScore) { chosen = Direction.Short; score = shortScore / votedWeight; }
        else { chosen = Direction.None; score = 0; }

        int confirmations = components.Count(c => c.Direction == chosen && c.Weight > 0);

        if (chosen != Direction.None)
        {
            // Never fade a decided 1h. A flat 1h is chop, not a veto — RSI/BB
            // mean-reversion is the setup we want there. RequireDecidedHtf
            // restores the old "wait for a decided 1h" lock if you want it.
            if (regime == Direction.None && _cfg.RequireDecidedHtf)
                vetoes.Add("no higher-timeframe regime - wait for a decided 1h");
            else if (regime != Direction.None && regime != chosen)
                vetoes.Add($"direction {chosen} fights the {regimeLabel} regime");
            if (score < _cfg.MinScore)
                vetoes.Add($"score {score:F2} < {_cfg.MinScore:F2}");

            // `score` is agreement among components that voted, so three weak
            // votes with everything else abstaining reads as a perfect 1.00 -
            // which is how SUI was entered at RSI 65.7 with a flat regime on
            // ema+vwap+volume totalling 0.42 of weight. Agreement is not
            // conviction: also require an absolute amount of weight behind the
            // direction, measured against the full 1.00 of available weight.
            var agreeingWeight = chosen == Direction.Long ? longScore : shortScore;
            if (agreeingWeight < _cfg.MinAgreeingWeight)
                vetoes.Add($"thin conviction - only {agreeingWeight:F2} of weight agrees " +
                           $"(need {_cfg.MinAgreeingWeight:F2}); {score:F2} score is just consensus among abstainers");
            if (confirmations < _cfg.MinConfirmations)
                vetoes.Add($"only {confirmations} confirmations, need {_cfg.MinConfirmations}");

            // Regime+EMA+VWAP can stay aligned for hours and print score=1.00
            // with no actual event (the ETH chase at RSI 63.8 / vol 0.15x).
            var triggerWeight = components
                .Where(c => c.IsTrigger && c.Direction == chosen)
                .Sum(c => c.Weight);
            if (triggerWeight < _cfg.MinTriggerWeight)
                vetoes.Add($"no fresh trigger (rsi/bb/volume/book/macd/stoch/structure weight {triggerWeight:F2} < {_cfg.MinTriggerWeight:F2})");

            if (volRatio > 0 && volRatio < _cfg.MinVolumeRatio)
                vetoes.Add($"dead bar volume {volRatio:F2}x < {_cfg.MinVolumeRatio:F2}x");

            if (trending && chosen == Direction.Long && rsiNow >= _cfg.MaxTrendRsiLong)
                vetoes.Add($"late long - RSI {rsiNow:F1} >= {_cfg.MaxTrendRsiLong:F0}, wait for a pullback");
            if (trending && chosen == Direction.Short && rsiNow <= _cfg.MinTrendRsiShort)
                vetoes.Add($"late short - RSI {rsiNow:F1} <= {_cfg.MinTrendRsiShort:F0}, wait for a bounce");

            if (trending)
            {
                var emaFight = components.FirstOrDefault(c =>
                    c.Name == "ema" && c.Direction != Direction.None && c.Direction != chosen && c.Weight >= 0.08);
                if (emaFight is not null)
                    vetoes.Add($"EMA fights a trend {chosen.ToString().ToLowerInvariant()}");
            }

            var bookVetoMin = _cfg.BookVetoMinImbalance > 0
                ? _cfg.BookVetoMinImbalance
                : _cfg.ImbalanceThreshold;
            if (imbDir != Direction.None && imbDir != chosen && Math.Abs(imb) >= bookVetoMin)
                vetoes.Add($"book {imbDir} fights the {chosen} setup");

            if (_cfg.RequireMacdAlign && i > 0 && Ta.IsUsable(macd.Histogram[i]))
            {
                var h = macd.Histogram[i];
                if (chosen == Direction.Long && h < 0)
                    vetoes.Add("MACD histogram is still negative - long is fighting momentum");
                if (chosen == Direction.Short && h > 0)
                    vetoes.Add("MACD histogram is still positive - short is fighting momentum");
            }

            StandAsideRules.Apply(vetoes, market, chosen, standAside, _cfg);
        }
        else
        {
            vetoes.Add("no directional edge");
        }

        return new TradeSignal
        {
            Market = market,
            Direction = chosen,
            Score = score,
            Price = price,
            Atr = atrNow,
            AtrPct = atrPct,
            SpreadBps = spreadBps == decimal.MaxValue ? 0 : spreadBps,
            Components = components,
            Vetoes = vetoes,
            Regime = regimeLabel,
            RegimeDirection = regime
        };
    }

    private static bool CrossedUp(double[] series, int i, double level, int lookback)
    {
        int from = Math.Max(1, i - lookback + 1);
        for (int k = from; k <= i; k++)
        {
            if (!Ta.IsUsable(series[k]) || !Ta.IsUsable(series[k - 1])) continue;
            if (series[k - 1] < level && series[k] >= level) return true;
        }
        return false;
    }

    private static bool CrossedDown(double[] series, int i, double level, int lookback)
    {
        int from = Math.Max(1, i - lookback + 1);
        for (int k = from; k <= i; k++)
        {
            if (!Ta.IsUsable(series[k]) || !Ta.IsUsable(series[k - 1])) continue;
            if (series[k - 1] > level && series[k] <= level) return true;
        }
        return false;
    }

    private static bool ReclaimedBand(double[] closes, double[] band, int i, bool fromBelow, int lookback)
    {
        int from = Math.Max(1, i - lookback + 1);
        for (int k = from; k <= i; k++)
        {
            if (!Ta.IsUsable(band[k]) || !Ta.IsUsable(band[k - 1])) continue;
            if (fromBelow && closes[k - 1] < band[k - 1] && closes[k] >= band[k]) return true;
            if (!fromBelow && closes[k - 1] > band[k - 1] && closes[k] <= band[k]) return true;
        }
        return false;
    }

    private static TradeSignal Blocked(string market, decimal price, string reason) => new()
    {
        Market = market,
        Direction = Direction.None,
        Score = 0,
        Price = price,
        Atr = 0,
        AtrPct = 0,
        SpreadBps = 0,
        Components = Array.Empty<SignalComponent>(),
        Vetoes = new[] { reason },
        RegimeDirection = Direction.None
    };
}
