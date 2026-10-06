using TraderApp.Trading;

namespace TraderApp.Tests;

public class TradeJournalAnalyticsTests
{
    [Fact]
    public void Report_separates_experiments_and_surfaces_execution_slippage()
    {
        var trades = new[]
        {
            new TradeRecord
            {
                Market = "AAVE_USDT", ClosedUtc = new DateTime(2026, 10, 1), NetPnl = 2m,
                RMultiple = 1m, EntrySlippageBps = 1.5m,
                Entry = new EntryContext(0.8, [], 1, 2, "trend", null, "", 12, "Monday", 1)
                { ExperimentId = "alpha-v1" }
            },
            new TradeRecord
            {
                Market = "SOL_USDT", ClosedUtc = new DateTime(2026, 10, 2), NetPnl = -1m,
                RMultiple = -0.5m,
                Entry = new EntryContext(0.7, [], 1, 2, "chop", null, "", 12, "Tuesday", 1)
                { ExperimentId = "beta-v1" }
            }
        };

        var report = TradeJournal.FormatReport(trades);

        Assert.Contains("Experiment alpha-v1", report);
        Assert.Contains("Experiment beta-v1", report);
        Assert.Contains("entry slip +1.5bp", report);
        Assert.Contains("Market AAVE_USDT", report);
    }
}
