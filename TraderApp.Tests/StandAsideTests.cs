using TraderApp.Configuration;
using TraderApp.Signals;

namespace TraderApp.Tests;

public class StandAsideTests
{
    private static SignalConfig Cfg(Action<SignalConfig>? tweak = null)
    {
        var c = new SignalConfig
        {
            StandAsideEnabled = true,
            AnchorMarket = "BTC_USDT",
            MaxFundingRate = 0.0002m
        };
        tweak?.Invoke(c);
        return c;
    }

    [Fact]
    public void Extreme_positive_funding_blocks_longs_only()
    {
        var snap = new StandAsideSnapshot(0.0004m, "SOL_PERP", Direction.None, false, "flat");
        var longVetoes = new List<string>();
        var shortVetoes = new List<string>();

        StandAsideRules.Apply(longVetoes, "SOL_USDT", Direction.Long, snap, Cfg());
        StandAsideRules.Apply(shortVetoes, "SOL_USDT", Direction.Short, snap, Cfg());

        Assert.Contains(longVetoes, v => v.Contains("crowded longs"));
        Assert.Empty(shortVetoes);
    }

    [Fact]
    public void Extreme_negative_funding_blocks_shorts_only()
    {
        var snap = new StandAsideSnapshot(-0.0004m, "SOL_PERP", Direction.None, false, "flat");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "SOL_USDT", Direction.Short, snap, Cfg());
        Assert.Contains(vetoes, v => v.Contains("crowded shorts"));
    }

    [Fact]
    public void Quiet_funding_does_not_veto()
    {
        var snap = new StandAsideSnapshot(0.00005m, "SOL_PERP", Direction.None, false, "flat");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "SOL_USDT", Direction.Long, snap, Cfg());
        Assert.Empty(vetoes);
    }

    [Fact]
    public void Btc_flip_against_alt_blocks_entry()
    {
        var snap = new StandAsideSnapshot(0.00005m, "SOL_PERP", Direction.Short, true, "down");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "SOL_USDT", Direction.Long, snap, Cfg());
        Assert.Contains(vetoes, v => v.Contains("flipped down against Long"));
    }

    [Fact]
    public void Stale_btc_downtrend_without_flip_is_not_a_new_veto()
    {
        var snap = new StandAsideSnapshot(0.00005m, "SOL_PERP", Direction.Short, false, "down");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "SOL_USDT", Direction.Long, snap, Cfg());
        Assert.Empty(vetoes);
    }

    [Fact]
    public void Anchor_market_does_not_veto_itself()
    {
        var snap = new StandAsideSnapshot(null, null, Direction.Short, true, "down");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "BTC_USDT", Direction.Long, snap, Cfg());
        Assert.Empty(vetoes);
    }

    [Fact]
    public void Disabled_stand_aside_is_a_no_op()
    {
        var snap = new StandAsideSnapshot(0.001m, "SOL_PERP", Direction.Short, true, "down");
        var vetoes = new List<string>();
        StandAsideRules.Apply(vetoes, "SOL_USDT", Direction.Long, snap, Cfg(c => c.StandAsideEnabled = false));
        Assert.Empty(vetoes);
    }

    [Fact]
    public void Perp_ticker_maps_spot_usdt()
    {
        Assert.Equal("SOL_PERP", StandAsideRules.PerpTicker("SOL_USDT"));
        Assert.Equal("AAVE_PERP", StandAsideRules.PerpTicker("AAVE_USDT"));
        Assert.Equal("SOLUSDT", StandAsideRules.PerpTicker("SOLUSDT"));
        Assert.Null(StandAsideRules.PerpTicker("SOLBTC"));
    }
}
