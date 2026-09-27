using TraderApp.Configuration;
using TraderApp.Risk;
using TraderApp.Signals;

namespace TraderApp.Tests;

/// <summary>
/// Confidence-gating and conviction sizing — the pieces adapted from the
/// "calibrated decision engine" architecture (state in code, abstain when
/// uncertain, size with confidence) without wiring an external model.
/// </summary>
public class JudgmentGateTests
{
    [Theory]
    [InlineData(0.58, 0.58, 0.75, 0.75)]   // at MinScore → floor
    [InlineData(1.00, 0.58, 0.75, 1.00)]   // perfect → full
    [InlineData(0.79, 0.58, 0.75, 0.875)]  // midpoint of [0.58, 1]
    [InlineData(0.90, 0.58, 1.00, 1.00)]   // floor disabled → always 1
    [InlineData(0.40, 0.58, 0.75, 0.75)]   // below MinScore still floors (signal would be vetoed anyway)
    public void ConvictionScale_maps_score_onto_floor_to_one(
        double score, double minScore, double floor, double expected)
    {
        var scale = RiskManager.ConvictionScale(score, minScore, (decimal)floor);
        Assert.Equal((decimal)expected, scale, precision: 3);
    }

    [Fact]
    public void Direction_edge_is_absolute_agree_minus_oppose()
    {
        var signal = new TradeSignal
        {
            Market = "SOL_USDT",
            Direction = Direction.Long,
            Score = 0.58,
            Price = 100,
            Atr = 1,
            AtrPct = 1,
            SpreadBps = 2,
            Components = Array.Empty<SignalComponent>(),
            Vetoes = Array.Empty<string>(),
            AgreeingWeight = 0.55,
            OpposingWeight = 0.40
        };

        Assert.Equal(0.15, signal.DirectionEdge, precision: 6);
    }

    [Fact]
    public void MinDirectionEdge_config_defaults_to_uncertainty_gate()
    {
        var cfg = new SignalConfig();
        Assert.Equal(0.18, cfg.MinDirectionEdge);
        Assert.True(cfg.MinDirectionEdge > 0);
    }

    [Fact]
    public void ConvictionRiskFloor_default_is_full_size_until_configured()
    {
        // Class default stays at 1 (off) so unit tests and missing config keys
        // do not silently shrink size; appsettings.json sets 0.75 for live.
        Assert.Equal(1m, new RiskConfig().ConvictionRiskFloor);
    }

    [Fact]
    public void BotConfig_rejects_out_of_range_judgment_knobs()
    {
        var cfg = new BotConfig
        {
            Api = { Key = "k", Secret = "s" },
            Trading = { Markets = { "SOL_USDT" } },
            Signals = { MinDirectionEdge = 1.5 },
            Risk = { ConvictionRiskFloor = -0.1m }
        };

        var errors = cfg.Validate();
        Assert.Contains(errors, e => e.Contains("MinDirectionEdge"));
        Assert.Contains(errors, e => e.Contains("ConvictionRiskFloor"));
    }
}
