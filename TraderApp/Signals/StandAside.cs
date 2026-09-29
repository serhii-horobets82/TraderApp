using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;

namespace TraderApp.Signals;

/// <summary>
/// Extra public-data context that is not a TA vote: predicted perp funding
/// and the BTC higher-timeframe. Used only to stand aside.
/// </summary>
public readonly record struct StandAsideSnapshot(
    decimal? FundingRate,
    string? FundingTicker,
    Direction AnchorRegime,
    bool AnchorFlipped,
    string AnchorLabel);

/// <summary>
/// Stand-aside rules. These never open a trade; they only refuse one.
/// Funding: longs pay when the predicted rate is very positive (crowded long).
/// Anchor: skip an alt that is fighting a freshly flipped BTC 1h.
/// </summary>
public static class StandAsideRules
{
    public static void Apply(
        ICollection<string> vetoes,
        string market,
        Direction chosen,
        StandAsideSnapshot? snapshot,
        SignalConfig cfg)
    {
        if (!cfg.StandAsideEnabled || snapshot is null || chosen == Direction.None)
            return;

        var ctx = snapshot.Value;

        if (ctx.FundingRate is { } funding && cfg.MaxFundingRate > 0)
        {
            var ticker = string.IsNullOrWhiteSpace(ctx.FundingTicker) ? "perp" : ctx.FundingTicker;
            if (chosen == Direction.Long && funding >= cfg.MaxFundingRate)
                vetoes.Add($"crowded longs — {ticker} funding {funding:P3} >= {cfg.MaxFundingRate:P3}");
            else if (chosen == Direction.Short && funding <= -cfg.MaxFundingRate)
                vetoes.Add($"crowded shorts — {ticker} funding {funding:P3} <= -{cfg.MaxFundingRate:P3}");
        }

        var isAnchor = string.Equals(market, cfg.AnchorMarket, StringComparison.OrdinalIgnoreCase);
        if (!isAnchor &&
            ctx.AnchorFlipped &&
            ctx.AnchorRegime != Direction.None &&
            ctx.AnchorRegime != chosen)
        {
            vetoes.Add($"{cfg.AnchorMarket} {cfg.AnchorInterval} flipped {ctx.AnchorLabel} against {chosen}");
        }
    }

    public static (Direction Dir, bool Flipped, string Label) ReadClosedRegime(
        IReadOnlyList<Candle> candles, int emaPeriod)
    {
        if (candles.Count < emaPeriod + 4)
            return (Direction.None, false, "flat");

        var closes = Ta.Closes(candles);
        var ema = Ta.Ema(closes, emaPeriod);
        var i = candles.Count - 2;
        var prev = i - 1;
        if (prev < 1 || !Ta.IsUsable(ema[i]) || !Ta.IsUsable(ema[prev]))
            return (Direction.None, false, "flat");

        var closed = ema.Take(i + 1).Where(Ta.IsUsable).ToArray();
        var slope = Ta.NormalisedSlope(closed, 3);

        Direction dir = Direction.None;
        var label = "flat";
        if (Ta.IsUsable(slope))
        {
            if (closes[i] > ema[i] && slope > 0) { dir = Direction.Long; label = "up"; }
            else if (closes[i] < ema[i] && slope < 0) { dir = Direction.Short; label = "down"; }
        }

        var flipped = (closes[i] > ema[i]) != (closes[prev] > ema[prev]);
        return (dir, flipped && dir != Direction.None, label);
    }

    public static string? PerpTicker(string spotMarket)
    {
        var usdt = spotMarket.IndexOf("_USDT", StringComparison.OrdinalIgnoreCase);
        if (usdt > 0) return spotMarket[..usdt] + "_PERP";
        // Bybit linear: funding lives on the same symbol (SOLUSDT).
        if (spotMarket.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
            !spotMarket.Contains('_', StringComparison.Ordinal))
            return spotMarket;
        return null;
    }
}

/// <summary>Loads funding + BTC regime from public endpoints. Cached inside the client.</summary>
public sealed class StandAsideReader
{
    private readonly BotConfig _cfg;
    private readonly IExchangeClient _client;

    public StandAsideReader(BotConfig cfg, IExchangeClient client)
    {
        _cfg = cfg;
        _client = client;
    }

    public async Task<StandAsideSnapshot?> LoadAsync(string market, CancellationToken ct)
    {
        if (!_cfg.Signals.StandAsideEnabled) return null;

        var futuresTask = _client.GetFuturesSnapshotAsync(ct);
        var anchorTask = _client.GetKlinesAsync(
            _cfg.Signals.AnchorMarket,
            string.IsNullOrWhiteSpace(_cfg.Signals.AnchorInterval)
                ? _cfg.Trading.RegimeInterval
                : _cfg.Signals.AnchorInterval,
            _cfg.Trading.CandleLookback,
            ct);

        await Task.WhenAll(futuresTask, anchorTask);

        var (dir, flipped, label) = StandAsideRules.ReadClosedRegime(
            await anchorTask, _cfg.Signals.EmaRegime);

        decimal? funding = null;
        string? ticker = null;
        var futures = await futuresTask;
        var perp = StandAsideRules.PerpTicker(market);
        if (perp is not null && futures.TryGet(perp, market, out var row))
        {
            funding = row.FundingRate;
            ticker = row.TickerId;
        }

        return new StandAsideSnapshot(funding, ticker, dir, flipped, label);
    }
}
