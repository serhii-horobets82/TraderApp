using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Trading;

namespace TraderApp.Tests;

public class VenueHostTests
{
    [Fact]
    public void Default_exchange_mode_is_whitebit_only()
    {
        var cfg = new BotConfig
        {
            Api = { Key = "k", Secret = "s" },
            Trading = { Markets = { "SOL_USDT" } }
        };
        Assert.Equal(ExchangeMode.WhiteBIT, cfg.ResolveExchangeMode());
        var venues = VenueHost.Build(cfg, ExchangeMode.WhiteBIT);
        Assert.Single(venues);
        Assert.Equal("WhiteBIT", venues[0].Label);
        Assert.IsType<WhiteBitClient>(venues[0].Client);
        venues[0].Client.Dispose();
    }

    [Fact]
    public void Both_mode_builds_two_isolated_runners()
    {
        var cfg = new BotConfig
        {
            Api = { Key = "wk", Secret = "ws" },
            Bybit = { Key = "bk", Secret = "bs", Markets = { "SOLUSDT" } },
            Trading =
            {
                Markets = { "SOL_USDT" },
                TradeJournalPath = "data/trades.jsonl",
                ExchangeMode = "Both"
            },
            Risk = { SessionStatePath = "data/session-state.json" }
        };

        var venues = VenueHost.Build(cfg, ExchangeMode.Both);
        Assert.Equal(2, venues.Count);
        Assert.Equal("WhiteBIT", venues[0].Label);
        Assert.Equal("Bybit", venues[1].Label);
        Assert.Contains("SOL_USDT", venues[0].Config.Trading.Markets);
        Assert.Contains("SOLUSDT", venues[1].Config.Trading.Markets);
        Assert.Equal("data/session-state.json", venues[0].Config.Risk.SessionStatePath);
        Assert.Equal("data/session-state-bybit.json", venues[1].Config.Risk.SessionStatePath);
        Assert.Equal("data/trades-bybit.jsonl", venues[1].Config.Trading.TradeJournalPath);
        Assert.Equal("BTCUSDT", venues[1].Config.Signals.AnchorMarket);

        foreach (var v in venues) v.Client.Dispose();
    }

    [Fact]
    public void Whitebit_only_validate_does_not_require_bybit_keys()
    {
        var cfg = new BotConfig
        {
            Api = { Key = "k", Secret = "s" },
            Trading = { Markets = { "SOL_USDT" }, ExchangeMode = "WhiteBIT" }
        };
        var errors = cfg.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("Bybit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bybit_interval_mapping()
    {
        Assert.Equal("15", BybitClient.ToBybitInterval("15m"));
        Assert.Equal("60", BybitClient.ToBybitInterval("1h"));
        Assert.Equal("D", BybitClient.ToBybitInterval("1d"));
    }
}
