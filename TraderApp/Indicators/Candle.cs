using System.Globalization;

namespace TraderApp.Indicators;

public sealed record Candle(
    DateTime OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal VolumeBase,
    decimal VolumeQuote)
{
    /// <summary>
    /// Parses one row of GET /api/v1/public/kline.
    ///
    /// The four price fields sit at indices 1..4 but the documented ordering and
    /// the documented sample disagree about whether it is open/close/high/low or
    /// open/high/low/close. Both readings agree that index 1 is the open, so we
    /// take the open from there and recover high/low as the max/min of the other
    /// three - which yields the correct close under either convention.
    /// </summary>
    public static Candle? FromKline(IReadOnlyList<object> row)
    {
        if (row.Count < 6) return null;
        if (!TryTimestamp(row[0], out var ts)) return null;

        Span<decimal> p = stackalloc decimal[3];
        if (!TryDec(row[1], out var open)) return null;
        for (int i = 0; i < 3; i++)
            if (!TryDec(row[i + 2], out p[i])) return null;

        decimal high = Math.Max(p[0], Math.Max(p[1], p[2]));
        decimal low = Math.Min(p[0], Math.Min(p[1], p[2]));

        // Whichever of the three is neither the max nor the min is the close.
        decimal close = p[0];
        for (int i = 0; i < 3; i++)
        {
            if (p[i] != high && p[i] != low) { close = p[i]; break; }
            // Degenerate candle (high == low == close): any of them is correct.
            if (high == low) { close = p[i]; break; }
        }

        TryDec(row.Count > 5 ? row[5] : 0m, out var volBase);
        TryDec(row.Count > 6 ? row[6] : 0m, out var volQuote);

        return new Candle(ts, open, high, low, close, volBase, volQuote);
    }

    private static bool TryTimestamp(object o, out DateTime utc)
    {
        utc = default;
        if (!TryDec(o, out var seconds) || seconds <= 0) return false;
        utc = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000m)).UtcDateTime;
        return true;
    }

    private static bool TryDec(object? o, out decimal value)
    {
        switch (o)
        {
            case null:
                value = 0; return false;
            case decimal d:
                value = d; return true;
            case double dd:
                value = (decimal)dd; return true;
            case long l:
                value = l; return true;
            case int i:
                value = i; return true;
            default:
                return decimal.TryParse(
                    Convert.ToString(o, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }

    public decimal TypicalPrice => (High + Low + Close) / 3m;
}
