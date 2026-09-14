using System.Globalization;

namespace TraderApp.Exchange;

/// <summary>
/// Price/quantity quantisation for one market.
///
/// From 2026-09-18 WhiteBIT rejects a limit price that is not a multiple of
/// tickSize and an amount that is not a multiple of stepSize. When the engine
/// supplies no tick/step (the field comes back as an empty string) we fall back
/// to the decimal precision fields, which is what the old behaviour effectively
/// assumed - except the old code hardcoded 8 and 4 decimals for every market.
/// </summary>
public sealed class MarketRules
{
    public string Market { get; }
    public decimal TickSize { get; }
    public decimal StepSize { get; }
    public int PriceDecimals { get; }
    public int AmountDecimals { get; }
    public decimal MinAmount { get; }
    public decimal MinTotal { get; }
    public decimal MaxTotal { get; }
    public decimal MakerFeePct { get; private set; }
    public decimal TakerFeePct { get; private set; }
    public bool IsCollateral { get; }
    public bool TradesEnabled { get; }
    public long? DelistedAt { get; }

    public MarketRules(MarketInfo info)
    {
        Market = info.Name;
        MinAmount = info.MinAmount;
        MinTotal = info.MinTotal;
        MaxTotal = info.MaxTotal;
        MakerFeePct = info.MakerFee;
        TakerFeePct = info.TakerFee;
        IsCollateral = info.IsCollateral;
        TradesEnabled = info.TradesEnabled;
        DelistedAt = info.DelistedAt;

        TickSize = ParseStep(info.TickSize, info.MoneyPrec);
        StepSize = ParseStep(info.StepSize, info.StockPrec);

        // Format with the precision the exchange declares, but never coarser
        // than the tick/step itself needs.
        PriceDecimals = Math.Max(DecimalsOf(TickSize), info.MoneyPrec);
        AmountDecimals = Math.Max(DecimalsOf(StepSize), info.StockPrec);
    }

    /// <summary>
    /// Replace the public default fees with the fee the account actually pays,
    /// from POST /api/v4/market/fee/single. The public markets endpoint reports
    /// the market default, which can be higher than an account's negotiated or
    /// tier-discounted rate - and the fee is what decides whether a scalp has
    /// any net edge at all.
    /// </summary>
    public void ApplyAccountFees(decimal makerPct, decimal takerPct)
    {
        if (makerPct != 0) MakerFeePct = makerPct;
        if (takerPct != 0) TakerFeePct = takerPct;
    }

    private static decimal ParseStep(string? raw, int precisionFallback)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0)
            return v;

        // 10^-precision, e.g. moneyPrec 2 -> 0.01
        var step = 1m;
        for (int i = 0; i < Math.Clamp(precisionFallback, 0, 18); i++) step /= 10m;
        return step;
    }

    private static int DecimalsOf(decimal value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        var dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }

    private static decimal Quantize(decimal value, decimal step, MidpointRounding _, bool up)
    {
        if (step <= 0) return value;
        var units = value / step;
        var rounded = up ? Math.Ceiling(units) : Math.Floor(units);
        return rounded * step;
    }

    /// <summary>Snap a price down to the nearest valid tick.</summary>
    public decimal FloorPrice(decimal price) => Quantize(price, TickSize, MidpointRounding.ToZero, up: false);

    /// <summary>Snap a price up to the nearest valid tick.</summary>
    public decimal CeilPrice(decimal price) => Quantize(price, TickSize, MidpointRounding.ToZero, up: true);

    /// <summary>
    /// Snap a price to a valid tick in the direction that is conservative for
    /// the given side: a buy rounds down (pay less), a sell rounds up.
    /// </summary>
    public decimal SnapPriceForSide(decimal price, bool isBuy) => isBuy ? FloorPrice(price) : CeilPrice(price);

    /// <summary>Snap a quantity down to the nearest valid step - never round size up past what risk allows.</summary>
    public decimal FloorAmount(decimal amount) => Quantize(amount, StepSize, MidpointRounding.ToZero, up: false);

    public string FormatPrice(decimal price) =>
        FloorPrice(price).ToString("F" + PriceDecimals, CultureInfo.InvariantCulture);

    public string FormatPriceRaw(decimal alreadySnapped) =>
        alreadySnapped.ToString("F" + PriceDecimals, CultureInfo.InvariantCulture);

    public string FormatAmount(decimal amount) =>
        FloorAmount(amount).ToString("F" + AmountDecimals, CultureInfo.InvariantCulture);

    /// <summary>Whether an order of this size at this price satisfies the market minimums.</summary>
    public bool IsTradable(decimal amount, decimal price, out string reason)
    {
        reason = "";
        if (!TradesEnabled) { reason = "market has trading disabled"; return false; }
        if (!IsCollateral) { reason = "market is not enabled for collateral trading"; return false; }
        if (DelistedAt is > 0)
        {
            reason = $"market is scheduled for delisting at {DateTimeOffset.FromUnixTimeSeconds(DelistedAt.Value):u}";
            return false;
        }
        var snapped = FloorAmount(amount);
        if (snapped < MinAmount || snapped <= 0)
        {
            reason = $"amount {snapped} below market minimum {MinAmount}";
            return false;
        }
        var total = snapped * price;
        if (total < MinTotal)
        {
            reason = $"notional {total:F4} below market minimum total {MinTotal}";
            return false;
        }
        if (MaxTotal > 0 && total > MaxTotal)
        {
            reason = $"notional {total:F4} above market maximum total {MaxTotal}";
            return false;
        }
        return true;
    }

    public override string ToString() =>
        $"{Market} tick={TickSize} step={StepSize} minAmt={MinAmount} minTotal={MinTotal} " +
        $"maker={MakerFeePct}% taker={TakerFeePct}% collateral={IsCollateral}";
}
