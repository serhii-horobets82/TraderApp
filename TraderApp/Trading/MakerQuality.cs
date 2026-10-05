using TraderApp.Configuration;
using TraderApp.Exchange;

namespace TraderApp.Trading;

/// <summary>
/// Conservative pre-flight for passive entries. A maker fee discount is useful
/// only when the order has enough opposing flow to be filled without joining a
/// thin, adverse-selection-prone queue.
/// </summary>
internal static class MakerQuality
{
    internal sealed record Verdict(bool Allowed, string Reason)
    {
        public static readonly Verdict Pass = new(true, "ok");
    }

    public static Verdict Evaluate(OrderBook book, bool isLong, decimal amount, TradingConfig cfg,
        DateTime nowUtc)
    {
        if (!cfg.MakerQualityEnabled) return Verdict.Pass;
        if (book.BestBid is null || book.BestAsk is null)
            return new Verdict(false, "empty book side");

        if (book.ReceivedAtUtc != DateTime.MinValue &&
            nowUtc - book.ReceivedAtUtc > TimeSpan.FromMilliseconds(cfg.MakerMaxBookAgeMs))
            return new Verdict(false, $"book age {(nowUtc - book.ReceivedAtUtc).TotalMilliseconds:F0}ms exceeds {cfg.MakerMaxBookAgeMs}ms");

        var touch = isLong ? book.Bids.FirstOrDefault() : book.Asks.FirstOrDefault();
        if (touch is null || touch.Count < 2 || touch[0] <= 0 || touch[1] <= 0)
            return new Verdict(false, "no usable touch queue");

        var queueQuote = touch[0] * touch[1];
        var orderQuote = touch[0] * amount;
        if (cfg.MakerMinTopLevelDepthUsd > 0 && queueQuote < cfg.MakerMinTopLevelDepthUsd)
            return new Verdict(false, $"touch depth {queueQuote:F0} is below {cfg.MakerMinTopLevelDepthUsd:F0} USDT");

        var queueFraction = orderQuote / queueQuote;
        if (queueFraction > cfg.MakerMaxQueueFraction)
            return new Verdict(false,
                $"order is {queueFraction:P0} of touch queue (max {cfg.MakerMaxQueueFraction:P0})");

        var imbalance = book.Imbalance(5);
        var directionalImbalance = isLong ? imbalance : -imbalance;
        if (directionalImbalance < (double)cfg.MakerMinDirectionalImbalance)
            return new Verdict(false,
                $"book imbalance {imbalance:P0} does not support {(isLong ? "long" : "short")} maker entry");

        return Verdict.Pass;
    }
}
