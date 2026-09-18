using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Signals;
using TraderApp.Trading;

namespace TraderApp.Backtest;

/// <summary>
/// Measures whether the entry signal predicts direction, independently of
/// stops, targets, fees and position sizing.
///
/// The backtest answers "did this configuration make money". That conflates
/// prediction with execution: a profitable exit rule can mask a useless signal,
/// and a bad exit rule can bury a good one. This answers the prior question -
/// when the engine says "long", does price actually go up more often than
/// chance, and by enough to cover a round trip?
///
/// Method: at every bar where a signal fires, record the return over the next
/// N bars measured in the signal's own direction. A signal with no skill gives
/// a mean of zero (minus drift) and a 50% hit rate. The standard error tells
/// you whether any deviation is real or a small-sample illusion.
/// </summary>
public static class SignalQuality
{
    private static readonly int[] Horizons = { 1, 2, 4, 8, 16 };

    private sealed record Observation(double[] ForwardPct, Direction Direction,
        IReadOnlyList<SignalComponent> Components);

    public static void Run(
        BotConfig cfg,
        IReadOnlyDictionary<string, (IReadOnlyList<Candle> entry, IReadOnlyList<Candle> regime)> data)
    {
        var engine = new SignalEngine(cfg.Signals);
        var signals = new List<Observation>();
        var baseline = new List<double[]>();   // unconditional moves, for drift reference

        foreach (var (market, series) in data)
        {
            var entry = series.entry;
            var regime = series.regime;
            var warmup = Math.Max(cfg.Signals.EmaRegime, cfg.Signals.AdxPeriod * 2 + 2) + 10;
            var lookback = Math.Max(cfg.Trading.CandleLookback, 60);
            var maxH = Horizons[^1];
            var regimeCursor = 0;

            for (int i = warmup; i < entry.Count - maxH - 1; i++)
            {
                var bar = entry[i];

                while (regimeCursor + 1 < regime.Count && regime[regimeCursor + 1].OpenTime <= bar.OpenTime)
                    regimeCursor++;
                if (regimeCursor + 1 < cfg.Signals.EmaRegime + 3) continue;

                // Entry happens at the next bar's open, exactly as the replay
                // and the live bot do, so the measured move is one we could
                // actually have captured.
                var basis = (double)entry[i + 1].Open;
                if (basis <= 0) continue;

                var fwd = new double[Horizons.Length];
                for (int h = 0; h < Horizons.Length; h++)
                    fwd[h] = ((double)entry[i + 1 + Horizons[h]].Close - basis) / basis * 100.0;

                baseline.Add(fwd);

                var signal = engine.Evaluate(market,
                    new ListWindow<Candle>(entry, i + 2, lookback),
                    new ListWindow<Candle>(regime, regimeCursor + 1, lookback),
                    SyntheticBook(bar.Close));

                if (!signal.IsActionable) continue;

                var sign = signal.Direction == Direction.Long ? 1.0 : -1.0;
                var directional = fwd.Select(x => x * sign).ToArray();
                signals.Add(new Observation(directional, signal.Direction, signal.Components));
            }
        }

        Report(cfg, signals, baseline);
    }

    private static void Report(BotConfig cfg, List<Observation> signals, List<double[]> baseline)
    {
        Console.WriteLine();
        Console.WriteLine("  ── Entry signal quality ────────────────────────────");
        if (signals.Count < 20)
        {
            Console.WriteLine($"  Only {signals.Count} signals - too few to measure. Widen the window or loosen filters.");
            return;
        }

        var roundTrip = cfg.Risk.RoundTripFeePct;
        Console.WriteLine($"  {signals.Count} signals over {baseline.Count} bars " +
                          $"({(double)signals.Count / baseline.Count * 100:F1}% of bars)");
        Console.WriteLine($"  A round trip costs {roundTrip:F2}%. To be tradable, mean move must exceed that.");
        Console.WriteLine();
        Console.WriteLine($"  {"horizon",9}{"mean %",10}{"std err",10}{"t",7}{"hit rate",10}{"vs drift",10}  verdict");
        Console.WriteLine("  " + new string('-', 68));

        for (int h = 0; h < Horizons.Length; h++)
        {
            var xs = signals.Select(s => s.ForwardPct[h]).ToArray();
            var mean = xs.Average();
            var sd = StdDev(xs, mean);
            var se = sd / Math.Sqrt(xs.Length);
            var t = se > 0 ? mean / se : 0;
            var hit = xs.Count(x => x > 0) / (double)xs.Length * 100.0;

            // Unconditional drift over the same horizon, so a market that simply
            // rose is not mistaken for predictive skill.
            var drift = baseline.Select(b => b[h]).Average();

            var verdict = Math.Abs(t) < 2 ? "indistinguishable from noise"
                : mean > (double)roundTrip ? "REAL and clears fees"
                : mean > 0 ? "real but below fee cost"
                : "significantly WRONG direction";

            Console.WriteLine($"  {Horizons[h] + " bars",9}{mean,10:F4}{se,10:F4}{t,7:F2}{hit,9:F1}%{drift,10:F4}  {verdict}");
        }

        Console.WriteLine();
        Console.WriteLine("  t is the mean divided by its standard error. |t| < 2 means the result is");
        Console.WriteLine("  consistent with chance; the sign of the mean is not meaningful there.");

        // Which components carry information, and which are noise the score is
        // paying attention to. This is what tells you what to drop.
        Console.WriteLine();
        Console.WriteLine("  ── Per-component directional value (8-bar horizon) ──");
        Console.WriteLine($"  {"component",12}{"votes",8}{"mean %",10}{"t",8}  verdict");
        Console.WriteLine("  " + new string('-', 52));

        const int hIdx = 3;   // 8 bars
        var names = signals.SelectMany(s => s.Components.Select(c => c.Name)).Distinct().OrderBy(n => n);

        foreach (var name in names)
        {
            var xs = new List<double>();
            foreach (var s in signals)
            {
                var c = s.Components.FirstOrDefault(c => c.Name == name);
                if (c is null || c.Direction == Direction.None) continue;
                // Score the component's own vote, not the trade's direction.
                var sign = c.Direction == Direction.Long ? 1.0 : -1.0;
                var tradeSign = s.Direction == Direction.Long ? 1.0 : -1.0;
                xs.Add(s.ForwardPct[hIdx] * sign * tradeSign);
            }
            if (xs.Count < 20) { Console.WriteLine($"  {name,12}{xs.Count,8}{"",10}{"",8}  too few votes"); continue; }

            var mean = xs.Average();
            var se = StdDev(xs, mean) / Math.Sqrt(xs.Count);
            var t = se > 0 ? mean / se : 0;
            var verdict = Math.Abs(t) < 2 ? "noise" : mean > 0 ? "informative" : "INVERTED - votes the wrong way";
            Console.WriteLine($"  {name,12}{xs.Count,8}{mean,10:F4}{t,8:F2}  {verdict}");
        }
    }

    private static double StdDev(IReadOnlyCollection<double> xs, double mean)
    {
        if (xs.Count < 2) return 0;
        double acc = 0;
        foreach (var x in xs) { var d = x - mean; acc += d * d; }
        return Math.Sqrt(acc / (xs.Count - 1));
    }

    private static OrderBook SyntheticBook(decimal mid)
    {
        var half = mid * 0.00015m;
        return new OrderBook
        {
            Bids = new List<List<decimal>> { new() { mid - half, 1m } },
            Asks = new List<List<decimal>> { new() { mid + half, 1m } }
        };
    }
}
