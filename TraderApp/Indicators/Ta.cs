namespace TraderApp.Indicators;

/// <summary>
/// Plain technical-analysis primitives. Each returns a series aligned to the
/// input (index i = value at candle i), with double.NaN where there is not yet
/// enough history, so callers never accidentally read a warm-up value.
/// </summary>
public static class Ta
{
    public static double[] Sma(IReadOnlyList<double> src, int period)
    {
        var outp = Filled(src.Count);
        if (period <= 0 || src.Count < period) return outp;

        double sum = 0;
        for (int i = 0; i < src.Count; i++)
        {
            sum += src[i];
            if (i >= period) sum -= src[i - period];
            if (i >= period - 1) outp[i] = sum / period;
        }
        return outp;
    }

    public static double[] Ema(IReadOnlyList<double> src, int period)
    {
        var outp = Filled(src.Count);
        if (period <= 0 || src.Count < period) return outp;

        double k = 2.0 / (period + 1);
        double seed = 0;
        for (int i = 0; i < period; i++) seed += src[i];
        double ema = seed / period;
        outp[period - 1] = ema;

        for (int i = period; i < src.Count; i++)
        {
            ema = src[i] * k + ema * (1 - k);
            outp[i] = ema;
        }
        return outp;
    }

    /// <summary>Wilder's RSI.</summary>
    public static double[] Rsi(IReadOnlyList<double> close, int period)
    {
        var outp = Filled(close.Count);
        if (period <= 0 || close.Count <= period) return outp;

        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            var ch = close[i] - close[i - 1];
            if (ch >= 0) gain += ch; else loss -= ch;
        }
        gain /= period;
        loss /= period;
        outp[period] = RsiValue(gain, loss);

        for (int i = period + 1; i < close.Count; i++)
        {
            var ch = close[i] - close[i - 1];
            var g = ch > 0 ? ch : 0;
            var l = ch < 0 ? -ch : 0;
            gain = (gain * (period - 1) + g) / period;
            loss = (loss * (period - 1) + l) / period;
            outp[i] = RsiValue(gain, loss);
        }
        return outp;
    }

    private static double RsiValue(double gain, double loss) =>
        loss <= 0 ? (gain <= 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);

    /// <summary>Wilder's ATR, in price units.</summary>
    public static double[] Atr(IReadOnlyList<Candle> candles, int period)
    {
        var outp = Filled(candles.Count);
        if (period <= 0 || candles.Count <= period) return outp;

        var tr = new double[candles.Count];
        tr[0] = (double)(candles[0].High - candles[0].Low);
        for (int i = 1; i < candles.Count; i++)
        {
            double h = (double)candles[i].High, l = (double)candles[i].Low, pc = (double)candles[i - 1].Close;
            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        double atr = 0;
        for (int i = 1; i <= period; i++) atr += tr[i];
        atr /= period;
        outp[period] = atr;

        for (int i = period + 1; i < candles.Count; i++)
        {
            atr = (atr * (period - 1) + tr[i]) / period;
            outp[i] = atr;
        }
        return outp;
    }

    /// <summary>Average Directional Index - trend strength, direction-agnostic.</summary>
    public static double[] Adx(IReadOnlyList<Candle> candles, int period)
    {
        var n = candles.Count;
        var outp = Filled(n);
        if (period <= 0 || n <= period * 2) return outp;

        var plusDm = new double[n];
        var minusDm = new double[n];
        var tr = new double[n];

        for (int i = 1; i < n; i++)
        {
            double up = (double)(candles[i].High - candles[i - 1].High);
            double down = (double)(candles[i - 1].Low - candles[i].Low);
            plusDm[i] = up > down && up > 0 ? up : 0;
            minusDm[i] = down > up && down > 0 ? down : 0;

            double h = (double)candles[i].High, l = (double)candles[i].Low, pc = (double)candles[i - 1].Close;
            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        double sTr = 0, sPlus = 0, sMinus = 0;
        for (int i = 1; i <= period; i++) { sTr += tr[i]; sPlus += plusDm[i]; sMinus += minusDm[i]; }

        var dx = Filled(n);
        for (int i = period + 1; i < n; i++)
        {
            sTr = sTr - sTr / period + tr[i];
            sPlus = sPlus - sPlus / period + plusDm[i];
            sMinus = sMinus - sMinus / period + minusDm[i];
            if (sTr <= 0) continue;

            double pdi = 100 * sPlus / sTr;
            double mdi = 100 * sMinus / sTr;
            double sum = pdi + mdi;
            dx[i] = sum <= 0 ? 0 : 100 * Math.Abs(pdi - mdi) / sum;
        }

        // ADX is the Wilder-smoothed DX.
        int firstDx = period + 1;
        int adxStart = firstDx + period - 1;
        if (adxStart >= n) return outp;

        double acc = 0;
        for (int i = firstDx; i < firstDx + period; i++) acc += double.IsNaN(dx[i]) ? 0 : dx[i];
        double adx = acc / period;
        outp[adxStart] = adx;

        for (int i = adxStart + 1; i < n; i++)
        {
            var v = double.IsNaN(dx[i]) ? 0 : dx[i];
            adx = (adx * (period - 1) + v) / period;
            outp[i] = adx;
        }
        return outp;
    }

    public readonly record struct BollingerBands(double[] Upper, double[] Middle, double[] Lower);

    public static BollingerBands Bollinger(IReadOnlyList<double> src, int period, double stdDevMult)
    {
        var mid = Sma(src, period);
        var up = Filled(src.Count);
        var low = Filled(src.Count);
        if (period <= 1 || src.Count < period) return new BollingerBands(up, mid, low);

        for (int i = period - 1; i < src.Count; i++)
        {
            double mean = mid[i];
            double acc = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                var d = src[j] - mean;
                acc += d * d;
            }
            var sd = Math.Sqrt(acc / period);
            up[i] = mean + stdDevMult * sd;
            low[i] = mean - stdDevMult * sd;
        }
        return new BollingerBands(up, mid, low);
    }

    /// <summary>Rolling VWAP over the last <paramref name="period"/> candles.</summary>
    public static double[] RollingVwap(IReadOnlyList<Candle> candles, int period)
    {
        var outp = Filled(candles.Count);
        if (period <= 0 || candles.Count < period) return outp;

        for (int i = period - 1; i < candles.Count; i++)
        {
            double pv = 0, v = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                var vol = (double)candles[j].VolumeBase;
                pv += (double)candles[j].TypicalPrice * vol;
                v += vol;
            }
            outp[i] = v <= 0 ? (double)candles[i].Close : pv / v;
        }
        return outp;
    }

    /// <summary>Slope of the last <paramref name="lookback"/> values, normalised by the level (per-bar fraction).</summary>
    public static double NormalisedSlope(IReadOnlyList<double> series, int lookback)
    {
        if (series.Count < lookback + 1) return double.NaN;
        var last = series[^1];
        var prev = series[^(lookback + 1)];
        if (double.IsNaN(last) || double.IsNaN(prev) || prev == 0) return double.NaN;
        return (last - prev) / Math.Abs(prev) / lookback;
    }

    public static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static double[] Filled(int n)
    {
        var a = new double[n];
        Array.Fill(a, double.NaN);
        return a;
    }

    public static double[] Closes(IReadOnlyList<Candle> c)
    {
        var a = new double[c.Count];
        for (int i = 0; i < c.Count; i++) a[i] = (double)c[i].Close;
        return a;
    }

    public static double[] Volumes(IReadOnlyList<Candle> c)
    {
        var a = new double[c.Count];
        for (int i = 0; i < c.Count; i++) a[i] = (double)c[i].VolumeBase;
        return a;
    }
}
