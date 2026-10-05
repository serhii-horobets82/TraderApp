using Newtonsoft.Json;

namespace TraderApp.Exchange;

// ── Request payloads ────────────────────────────────────────────────────
// Every signed request carries request/nonce. nonceWindow lets the server
// accept slightly out-of-order nonces; we keep it off and serialise nonces
// ourselves so it is never needed.

public class Payload
{
    [JsonProperty("request")] public string Request { get; set; } = "";
    [JsonProperty("nonce")] public string Nonce { get; set; } = "";
    [JsonProperty("nonceWindow")] public bool NonceWindow { get; set; }
}

public sealed class MarketPayload : Payload
{
    [JsonProperty("market", NullValueHandling = NullValueHandling.Ignore)]
    public string? Market { get; set; }
}

/// <summary>POST /api/v4/order/collateral/limit</summary>
public sealed class CollateralLimitOrderPayload : Payload
{
    [JsonProperty("market")] public string Market { get; set; } = "";
    [JsonProperty("side")] public string Side { get; set; } = "";

    // The API documents amount and price as strings; sending them as JSON
    // numbers risks the server re-formatting them and breaking tick alignment.
    [JsonProperty("amount")] public string Amount { get; set; } = "";
    [JsonProperty("price")] public string Price { get; set; } = "";

    [JsonProperty("clientOrderId", NullValueHandling = NullValueHandling.Ignore)]
    public string? ClientOrderId { get; set; }

    /// <summary>Attaching stopLoss creates an OTO order that survives this process dying.</summary>
    [JsonProperty("stopLoss", NullValueHandling = NullValueHandling.Ignore)]
    public string? StopLoss { get; set; }

    [JsonProperty("takeProfit", NullValueHandling = NullValueHandling.Ignore)]
    public string? TakeProfit { get; set; }

    [JsonProperty("postOnly", NullValueHandling = NullValueHandling.Ignore)]
    public bool? PostOnly { get; set; }

    [JsonProperty("ioc", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Ioc { get; set; }

    /// <summary>Added to the collateral order endpoints on 2026-04-22. Guarantees an exit cannot open a new position.</summary>
    [JsonProperty("reduceOnly", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ReduceOnly { get; set; }

    /// <summary>LONG / SHORT / BOTH. Required when the account is in hedge mode.</summary>
    [JsonProperty("positionSide", NullValueHandling = NullValueHandling.Ignore)]
    public string? PositionSide { get; set; }

    /// <summary>Self-trade prevention. Legacy values (cancel_both/cancel_new/cancel_old) were deprecated 2026-07-03.</summary>
    [JsonProperty("stp", NullValueHandling = NullValueHandling.Ignore)]
    public string? Stp { get; set; }
}

/// <summary>POST /api/v4/order/collateral/market</summary>
public sealed class CollateralMarketOrderPayload : Payload
{
    [JsonProperty("market")] public string Market { get; set; } = "";
    [JsonProperty("side")] public string Side { get; set; } = "";
    [JsonProperty("amount")] public string Amount { get; set; } = "";

    [JsonProperty("clientOrderId", NullValueHandling = NullValueHandling.Ignore)]
    public string? ClientOrderId { get; set; }

    [JsonProperty("reduceOnly", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ReduceOnly { get; set; }

    [JsonProperty("positionSide", NullValueHandling = NullValueHandling.Ignore)]
    public string? PositionSide { get; set; }

    [JsonProperty("stp", NullValueHandling = NullValueHandling.Ignore)]
    public string? Stp { get; set; }
}

/// <summary>POST /api/v4/collateral-account/position/close</summary>
public sealed class ClosePositionPayload : Payload
{
    [JsonProperty("positionId")] public long PositionId { get; set; }
    [JsonProperty("market")] public string Market { get; set; } = "";

    [JsonProperty("positionSide", NullValueHandling = NullValueHandling.Ignore)]
    public string? PositionSide { get; set; }
}

/// <summary>POST /api/v4/order/cancel</summary>
public sealed class CancelOrderPayload : Payload
{
    [JsonProperty("market")] public string Market { get; set; } = "";
    [JsonProperty("orderId")] public long OrderId { get; set; }
}

/// <summary>Paged private request.</summary>
public sealed class LimitPayload : Payload
{
    [JsonProperty("limit")] public int Limit { get; set; }
}

/// <summary>POST /api/v4/collateral-account/leverage</summary>
public sealed class LeveragePayload : Payload
{
    [JsonProperty("leverage")] public int Leverage { get; set; }
}

// ── Response models ─────────────────────────────────────────────────────

/// <summary>
/// Response of /api/v4/collateral-account/positions/open.
/// Note: <see cref="Pnl"/> is realised PnL from partial closes - the live
/// mark-to-market number is <see cref="UnrealizedPnl"/>. The original code
/// conflated the two. <c>positionSide</c> replaced the removed field of the
/// same name on position history in the 2026-04-29 change; here it is the
/// hedge-mode direction.
/// </summary>
public sealed class Position
{
    [JsonProperty("positionId")] public long PositionId { get; set; }
    [JsonProperty("market")] public string? Market { get; set; }

    /// <summary>Signed size: positive = long, negative = short.</summary>
    [JsonProperty("amount")] public decimal Amount { get; set; }

    [JsonProperty("basePrice")] public decimal BasePrice { get; set; }
    [JsonProperty("liquidationPrice")] public decimal? LiquidationPrice { get; set; }
    [JsonProperty("liquidationState")] public string? LiquidationState { get; set; }

    /// <summary>Realised PnL banked from partial closes.</summary>
    [JsonProperty("pnl")] public decimal Pnl { get; set; }

    /// <summary>Floating mark-to-market PnL of the still-open size.</summary>
    [JsonProperty("unrealizedPnl")] public decimal UnrealizedPnl { get; set; }

    [JsonProperty("pnlPercent")] public decimal PnlPercent { get; set; }
    [JsonProperty("margin")] public decimal Margin { get; set; }
    [JsonProperty("freeMargin")] public decimal FreeMargin { get; set; }
    [JsonProperty("funding")] public decimal Funding { get; set; }
    [JsonProperty("unrealizedFunding")] public decimal UnrealizedFunding { get; set; }
    [JsonProperty("positionSide")] public string? PositionSide { get; set; }
    [JsonProperty("openDate")] public double OpenDate { get; set; }
    [JsonProperty("modifyDate")] public double ModifyDate { get; set; }

    public bool IsLong => Amount > 0;
    public decimal AbsAmount => Math.Abs(Amount);

    /// <summary>
    /// WhiteBIT sometimes reports unrealizedPnl as 0 on tiny notionals.
    /// Fall back to mark − entry so logs and win/loss accounting stay honest.
    /// </summary>
    public decimal FloatingPnl(decimal mark) =>
        UnrealizedPnl != 0 ? UnrealizedPnl : (mark - BasePrice) * Amount;

    /// <summary>
    /// When this position was opened according to the venue, or null when the
    /// venue did not say. A missing value must stay null: the old conversion
    /// turned 0 into 1970-01-01, which made a fresh position look 56 years old.
    /// </summary>
    public DateTime? OpenedAtUtc => OpenDate > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(OpenDate * 1000)).UtcDateTime
        : null;

    public bool InTrouble =>
        string.Equals(LiquidationState, "margin_call", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(LiquidationState, "liquidation", StringComparison.OrdinalIgnoreCase);
}

public sealed class OrderResponse
{
    /// <summary>
    /// Venue order id, always a string. Bybit ids are UUIDs; WhiteBIT ids are
    /// integers that Json.NET coerces to their decimal text. An empty value
    /// means the venue did not return an id - it must never be mapped to a
    /// placeholder such as "0", which would match unrelated orders.
    /// </summary>
    [JsonProperty("orderId", NullValueHandling = NullValueHandling.Ignore)] public string OrderId { get; set; } = "";
    [JsonProperty("clientOrderId")] public string? ClientOrderId { get; set; }
    [JsonProperty("market")] public string? Market { get; set; }
    [JsonProperty("side")] public string? Side { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("amount")] public decimal Amount { get; set; }
    [JsonProperty("left")] public decimal Left { get; set; }
    [JsonProperty("dealStock")] public decimal DealStock { get; set; }
    [JsonProperty("dealMoney")] public decimal DealMoney { get; set; }
    [JsonProperty("price")] public decimal Price { get; set; }
    [JsonProperty("status")] public string? Status { get; set; }
    [JsonProperty("reduceOnly")] public bool ReduceOnly { get; set; }
}

/// <summary>
/// Response of POST /api/v4/market/fee/single - the fee this account actually
/// pays on a market, which may be below the public default shown by
/// /api/v4/public/markets.
/// </summary>
public sealed class MarketFee
{
    [JsonProperty("error")] public string? Error { get; set; }
    [JsonProperty("maker")] public decimal Maker { get; set; }
    [JsonProperty("taker")] public decimal Taker { get; set; }
}

/// <summary>
/// One record of POST /api/v4/collateral-account/positions/closed-pnl
/// (added 2026-07-28). This is the authoritative realised result of a closed
/// position - inferring it from equity deltas is unreliable when more than one
/// position is open.
/// </summary>
public sealed class ClosedPnl
{
    [JsonProperty("positionId")] public long PositionId { get; set; }
    [JsonProperty("market")] public string? Market { get; set; }

    /// <summary>
    /// Id of the closing order where the venue provides one (Bybit). Together
    /// with the timestamps it identifies one record, so the same record is
    /// never booked for two trades.
    /// </summary>
    [JsonIgnore] public string OrderId { get; set; } = "";

    /// <summary>Stable identity of this record, for "already booked" bookkeeping.</summary>
    public string RecordKey =>
        $"{PositionId}|{Market}|{OrderId}|{CloseDate}|{ClosedSize}|{NetPnl}";

    /// <summary>Realised PnL before funding, but after trade fees.</summary>
    [JsonProperty("grossPnl")] public decimal GrossPnl { get; set; }

    /// <summary>grossPnl minus funding. The number that actually hit the balance.</summary>
    [JsonProperty("netPnl")] public decimal NetPnl { get; set; }

    [JsonProperty("totalTradeFee")] public decimal TotalTradeFee { get; set; }
    [JsonProperty("totalFunding")] public decimal TotalFunding { get; set; }
    [JsonProperty("avgEntryPrice")] public decimal AvgEntryPrice { get; set; }
    [JsonProperty("avgExitPrice")] public decimal AvgExitPrice { get; set; }
    [JsonProperty("closedSize")] public decimal ClosedSize { get; set; }
    [JsonProperty("side")] public string? Side { get; set; }
    [JsonProperty("openDate")] public long OpenDate { get; set; }
    [JsonProperty("closeDate")] public long CloseDate { get; set; }

    /// <summary>
    /// Sums several records that belong to one trade (a partial take plus the
    /// final close). Prices are size-weighted; the close date is the latest.
    /// </summary>
    public static ClosedPnl Merge(IReadOnlyList<ClosedPnl> records)
    {
        if (records.Count == 1) return records[0];

        var size = records.Sum(r => r.ClosedSize);
        decimal Weighted(Func<ClosedPnl, decimal> price) =>
            size > 0 ? records.Sum(r => price(r) * r.ClosedSize) / size : price(records[0]);

        return new ClosedPnl
        {
            PositionId = records[0].PositionId,
            Market = records[0].Market,
            OrderId = string.Join(",", records.Select(r => r.OrderId).Where(o => o.Length > 0)),
            GrossPnl = records.Sum(r => r.GrossPnl),
            NetPnl = records.Sum(r => r.NetPnl),
            TotalTradeFee = records.Sum(r => r.TotalTradeFee),
            TotalFunding = records.Sum(r => r.TotalFunding),
            AvgEntryPrice = Weighted(r => r.AvgEntryPrice),
            AvgExitPrice = Weighted(r => r.AvgExitPrice),
            ClosedSize = size,
            Side = records[0].Side,
            OpenDate = records.Select(r => r.OpenDate).Where(d => d > 0).DefaultIfEmpty(0).Min(),
            CloseDate = records.Max(r => r.CloseDate)
        };
    }

    /// <summary>How long the position was actually held.</summary>
    public TimeSpan Held => OpenDate > 0 && CloseDate > OpenDate
        ? TimeSpan.FromSeconds(CloseDate - OpenDate)
        : TimeSpan.Zero;
}

/// <summary>Response of /api/v4/collateral-account/summary.</summary>
public sealed class CollateralSummary
{
    [JsonProperty("equity")] public decimal Equity { get; set; }
    [JsonProperty("margin")] public decimal Margin { get; set; }
    [JsonProperty("freeMargin")] public decimal FreeMargin { get; set; }
    [JsonProperty("pnl")] public decimal Pnl { get; set; }
    [JsonProperty("unrealizedFunding")] public decimal UnrealizedFunding { get; set; }
    [JsonProperty("leverage")] public int Leverage { get; set; }
    [JsonProperty("marginFraction")] public decimal MarginFraction { get; set; }
}

/// <summary>
/// One entry of GET /api/v4/public/markets.
/// tickSize and stepSize were added 2026-08-19; from 2026-09-18 orders whose
/// price or amount is not a multiple of them are rejected.
/// </summary>
public sealed class MarketInfo
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("stock")] public string Stock { get; set; } = "";
    [JsonProperty("money")] public string Money { get; set; } = "";
    [JsonProperty("stockPrec")] public int StockPrec { get; set; }
    [JsonProperty("moneyPrec")] public int MoneyPrec { get; set; }
    [JsonProperty("makerFee")] public decimal MakerFee { get; set; }
    [JsonProperty("takerFee")] public decimal TakerFee { get; set; }
    [JsonProperty("minAmount")] public decimal MinAmount { get; set; }
    [JsonProperty("minTotal")] public decimal MinTotal { get; set; }
    [JsonProperty("maxTotal")] public decimal MaxTotal { get; set; }
    [JsonProperty("tradesEnabled")] public bool TradesEnabled { get; set; }
    [JsonProperty("isCollateral")] public bool IsCollateral { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("delistedAt")] public long? DelistedAt { get; set; }

    /// <summary>May be an empty string when the trade engine supplies no value.</summary>
    [JsonProperty("tickSize")] public string? TickSize { get; set; }

    [JsonProperty("stepSize")] public string? StepSize { get; set; }
}

/// <summary>Response of GET /api/v4/public/orderbook/{market}.</summary>
public sealed class OrderBook
{
    [JsonProperty("ticker_id")] public string? TickerId { get; set; }
    [JsonProperty("timestamp")] public long Timestamp { get; set; }
    [JsonProperty("asks")] public List<List<decimal>> Asks { get; set; } = new();
    [JsonProperty("bids")] public List<List<decimal>> Bids { get; set; } = new();

    public decimal? BestAsk => Asks.Count > 0 && Asks[0].Count > 0 ? Asks[0][0] : null;
    public decimal? BestBid => Bids.Count > 0 && Bids[0].Count > 0 ? Bids[0][0] : null;

    public decimal? Mid
    {
        get
        {
            var (b, a) = (BestBid, BestAsk);
            return b.HasValue && a.HasValue ? (b.Value + a.Value) / 2m : null;
        }
    }

    public decimal? SpreadBps
    {
        get
        {
            var (b, a, m) = (BestBid, BestAsk, Mid);
            if (!b.HasValue || !a.HasValue || !m.HasValue || m.Value <= 0) return null;
            return (a.Value - b.Value) / m.Value * 10_000m;
        }
    }

    /// <summary>Resting notional (price × amount, in quote) on the thinner side of the top <paramref name="depth"/> levels.</summary>
    public decimal ThinSideDepthQuote(int depth)
    {
        decimal bid = 0, ask = 0;
        for (int i = 0; i < Math.Min(depth, Bids.Count); i++)
            if (Bids[i].Count > 1) bid += Bids[i][0] * Bids[i][1];
        for (int i = 0; i < Math.Min(depth, Asks.Count); i++)
            if (Asks[i].Count > 1) ask += Asks[i][0] * Asks[i][1];
        return Math.Min(bid, ask);
    }

    /// <summary>
    /// (bidVolume - askVolume) / total over the top <paramref name="depth"/> levels.
    /// Positive means resting buy interest dominates.
    /// </summary>
    public double Imbalance(int depth)
    {
        decimal bid = 0, ask = 0;
        for (int i = 0; i < Math.Min(depth, Bids.Count); i++)
            if (Bids[i].Count > 1) bid += Bids[i][1];
        for (int i = 0; i < Math.Min(depth, Asks.Count); i++)
            if (Asks[i].Count > 1) ask += Asks[i][1];
        var total = bid + ask;
        return total <= 0 ? 0 : (double)((bid - ask) / total);
    }
}

/// <summary>One row of GET /api/v4/public/futures.</summary>
public sealed class FuturesMarket
{
    [JsonProperty("ticker_id")] public string TickerId { get; set; } = "";
    [JsonProperty("stock_currency")] public string StockCurrency { get; set; } = "";
    [JsonProperty("funding_rate")] public decimal FundingRate { get; set; }
}

/// <summary>Cached public futures book: lookup by BTC_PERP or by SOL from SOL_USDT.</summary>
public sealed class FuturesSnapshot
{
    public static readonly FuturesSnapshot Empty = new();

    private readonly Dictionary<string, FuturesMarket> _byTicker =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FuturesMarket> _byStock =
        new(StringComparer.OrdinalIgnoreCase);

    public FuturesSnapshot() { }

    public FuturesSnapshot(IEnumerable<FuturesMarket> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TickerId)) continue;
            _byTicker[row.TickerId] = row;
            if (!string.IsNullOrWhiteSpace(row.StockCurrency) &&
                !_byStock.ContainsKey(row.StockCurrency))
                _byStock[row.StockCurrency] = row;
        }
    }

    public bool TryGet(string perpTicker, string spotMarket, out FuturesMarket row)
    {
        if (_byTicker.TryGetValue(perpTicker, out row!)) return true;
        var usdt = spotMarket.IndexOf('_');
        if (usdt > 0 && _byStock.TryGetValue(spotMarket[..usdt], out row!)) return true;
        row = null!;
        return false;
    }
}
