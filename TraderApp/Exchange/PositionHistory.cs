namespace TraderApp.Exchange;

/// <summary>One fill that changed the position size. Quantity is signed: buys positive, sells negative.</summary>
public readonly record struct PositionFill(DateTime TimeUtc, decimal SignedQty, long Sequence = 0);

/// <summary>
/// Works out when the position currently held was opened by replaying fills
/// backwards from its present size.
///
/// This exists because Bybit's position <c>createdTime</c> is the first time
/// the symbol was ever positioned, not when the current position opened. Any
/// reused symbol therefore looked hours or days old and hit the time stop on
/// the first tick. The fill history is the only per-trade source that is
/// always available.
/// </summary>
public static class PositionHistory
{
    /// <summary>
    /// Walks fills newest to oldest, undoing each from the current size. The
    /// fill after which the size would have been flat (or on the other side,
    /// for a reversal in one order) is the one that opened this position.
    /// Returns null when the history runs out before reaching a flat point:
    /// the true opening is older than what was supplied, and guessing would
    /// recreate the bug this class exists to prevent.
    /// </summary>
    public static DateTime? FindOpenTime(decimal currentSignedSize, IEnumerable<PositionFill> fills)
    {
        if (currentSignedSize == 0) return null;

        var remaining = currentSignedSize;
        foreach (var fill in fills
                     .Where(f => f.SignedQty != 0)
                     .OrderByDescending(f => f.TimeUtc)
                     .ThenByDescending(f => f.Sequence))
        {
            var before = remaining - fill.SignedQty;
            if (before == 0 || Math.Sign(before) != Math.Sign(remaining))
                return fill.TimeUtc;
            remaining = before;
        }

        return null;
    }
}
