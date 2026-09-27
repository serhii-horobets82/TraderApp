using TraderApp.Indicators;

namespace TraderApp.Exchange;

/// <summary>
/// Venue-agnostic trading surface used by <c>AutoTrader</c> / <c>SignalMonitor</c>.
/// WhiteBIT and Bybit each map their REST shapes onto the shared domain models.
/// </summary>
public interface IExchangeClient : IDisposable
{
    /// <summary>Stable id for logs and session files: "WhiteBIT" or "Bybit".</summary>
    string ExchangeId { get; }

    Task<IReadOnlyDictionary<string, MarketRules>> GetMarketRulesAsync(CancellationToken ct);
    Task<OrderBook?> GetOrderBookAsync(string market, int limit, CancellationToken ct);
    Task<IReadOnlyList<Candle>> GetKlinesAsync(
        string market, string interval, int limit, CancellationToken ct, bool closedOnly = true);
    Task<IReadOnlyList<Candle>> GetKlinesRangeAsync(
        string market, string interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<FuturesSnapshot> GetFuturesSnapshotAsync(CancellationToken ct);

    Task<CollateralSummary?> GetAccountSummaryAsync(CancellationToken ct);
    Task<object?> SetLeverageAsync(int leverage, CancellationToken ct);
    Task<MarketFee?> GetMarketFeeAsync(string market, CancellationToken ct);
    Task<IReadOnlyList<Position>> GetOpenPositionsAsync(string? market, CancellationToken ct);
    Task<IReadOnlyList<ClosedPnl>> GetClosedPnlAsync(int limit, CancellationToken ct);

    Task<OrderResponse?> PlaceEntryLimitAsync(EntryLimitRequest request, CancellationToken ct);
    Task<OrderResponse?> PlaceReduceOnlyMarketAsync(ReduceOnlyMarketRequest request, CancellationToken ct);
    Task<object?> ClosePositionAsync(long positionId, string market, string? positionSide, CancellationToken ct);
    Task<OrderResponse?> CancelOrderAsync(string market, long orderId, CancellationToken ct);
    Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string market, CancellationToken ct);
}

/// <summary>Exchange-agnostic entry limit (SL/TP attached when the venue supports OTO).</summary>
public sealed class EntryLimitRequest
{
    public required string Market { get; init; }
    public required string Side { get; init; }
    public required string Amount { get; init; }
    public required string Price { get; init; }
    public string? ClientOrderId { get; init; }
    public string? StopLoss { get; init; }
    public string? TakeProfit { get; init; }
    public bool PostOnly { get; init; }
}

/// <summary>Exchange-agnostic reduce-only market (partial take / flatten fragment).</summary>
public sealed class ReduceOnlyMarketRequest
{
    public required string Market { get; init; }
    public required string Side { get; init; }
    public required string Amount { get; init; }
    public string? ClientOrderId { get; init; }
    public string? PositionSide { get; init; }
}

/// <summary>Shared kline interval parser (WhiteBIT and Bybit both use 15m / 1h style strings).</summary>
public static class CandleInterval
{
    public static TimeSpan Parse(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval) || interval.Length < 2) return TimeSpan.Zero;

        var unit = interval[^1];
        if (!int.TryParse(interval[..^1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
            return TimeSpan.Zero;

        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(7 * n),
            'M' => TimeSpan.FromDays(30 * n),
            _ => TimeSpan.Zero
        };
    }
}

/// <summary>Stable synthetic position id for venues that do not expose WhiteBIT-style positionId.</summary>
public static class SyntheticPositionId
{
    public static long For(string market, bool isLong)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(market + (isLong ? ":L" : ":S"));
        // Keep non-zero positive so logs never show "#0".
        return (hash & 0x7FFF_FFFFL) + 1;
    }
}
