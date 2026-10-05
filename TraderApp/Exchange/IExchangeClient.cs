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

    /// <summary>
    /// Asset ticker → balance held as collateral, in native units. Lets the
    /// trader warn when collateral is not quote-only (price moves in those
    /// assets would otherwise read as trading PnL in the equity locks).
    /// Empty when the venue cannot report it.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetCollateralBalancesAsync(CancellationToken ct);

    Task<object?> SetLeverageAsync(int leverage, CancellationToken ct);
    Task<MarketFee?> GetMarketFeeAsync(string market, CancellationToken ct);
    Task<IReadOnlyList<Position>> GetOpenPositionsAsync(string? market, CancellationToken ct);
    Task<IReadOnlyList<ClosedPnl>> GetClosedPnlAsync(int limit, CancellationToken ct);

    Task<OrderResponse?> PlaceEntryLimitAsync(EntryLimitRequest request, CancellationToken ct);
    Task<OrderResponse?> PlaceReduceOnlyMarketAsync(ReduceOnlyMarketRequest request, CancellationToken ct);
    Task<object?> ClosePositionAsync(long positionId, string market, string? positionSide, CancellationToken ct);
    Task<OrderResponse?> CancelOrderAsync(string market, string orderId, CancellationToken ct);
    Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string market, CancellationToken ct);

    /// <summary>
    /// Final-state lookup for an order that is no longer on the open book, so
    /// "filled" can be told apart from "cancelled / rejected". Null when the
    /// venue cannot say; callers then fall back to the position list.
    /// </summary>
    Task<OrderResponse?> GetOrderAsync(string market, string orderId, CancellationToken ct) =>
        Task.FromResult<OrderResponse?>(null);

    /// <summary>
    /// When the position currently held on this market was actually opened,
    /// reconstructed from execution history. Null when it cannot be determined
    /// (no history, or the history does not reach back to a flat position).
    /// </summary>
    Task<DateTime?> ResolvePositionOpenedAsync(Position position, CancellationToken ct) =>
        Task.FromResult<DateTime?>(null);
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

    /// <summary>
    /// How long a closed-bar kline response stays valid. The newest closed bar
    /// opened at <paramref name="lastClosedOpen"/>; nothing new can appear
    /// until the bar after it closes, i.e. two durations later.
    ///
    /// The previous formula used one duration - the bar's own close time, which
    /// is always already in the past for a closed bar - so the cache was never
    /// populated and every 2s tick re-downloaded 15m and 1h history for every
    /// market. On Bybit that showed up as retCode 10006 (rate limited) within
    /// seconds of boot.
    ///
    /// Right after a boundary the exchange may still be finalising the bar or
    /// not yet have emitted the forming one, so for the first few seconds the
    /// result is only trusted briefly.
    /// </summary>
    public static DateTime ClosedBarValidUntil(DateTime lastClosedOpen, TimeSpan duration, DateTime nowUtc)
    {
        if (duration <= TimeSpan.Zero) return nowUtc.AddSeconds(5);

        var closedAt = lastClosedOpen + duration;
        if (nowUtc - closedAt < TimeSpan.FromSeconds(5))
            return nowUtc.AddSeconds(5);

        // Stale data (older than one full bar behind) is not worth holding.
        return closedAt + duration;
    }
}

/// <summary>Stable synthetic position id for venues that do not expose WhiteBIT-style positionId.</summary>
public static class SyntheticPositionId
{
    public static long For(string market, bool isLong)
    {
        // FNV-1a, because string.GetHashCode is randomised per process: the id
        // must be identical after a restart or persisted position state could
        // never be matched back to the position.
        var key = (market + (isLong ? ":L" : ":S")).ToUpperInvariant();
        uint hash = 2166136261;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        // Keep non-zero positive so logs never show "#0".
        return (hash & 0x7FFF_FFFFL) + 1;
    }
}
