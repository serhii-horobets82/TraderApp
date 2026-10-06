using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraderApp.Signals;

namespace TraderApp.Trading;

/// <summary>
/// What the bot knew when it pressed the button. Captured at order time and
/// carried to the close so a trade can be graded against its own setup,
/// not just its PnL. Extra weight / ADX fields are the calibration triple
/// inputs: predicted confidence vs realised outcome.
/// </summary>
public sealed record EntryContext(
    double Score,
    IReadOnlyList<string> Components,
    decimal AtrPct,
    decimal SpreadBps,
    string Regime,
    decimal? FundingRate,
    string AnchorRegime,
    int HourUtc,
    string DayOfWeek,
    decimal RiskScale,
    double AgreeingWeight = 0,
    double OpposingWeight = 0,
    double TriggerWeight = 0,
    double Adx = 0,
    decimal ConvictionScale = 1m)
{
    public string ExperimentId { get; init; } = "";
    public decimal IntendedEntryPrice { get; init; }
    public decimal IntendedStopPrice { get; init; }
    public decimal IntendedRiskQuote { get; init; }

    public static EntryContext From(
        TradeSignal s, StandAsideSnapshot? aside, decimal riskScale, DateTime nowUtc,
        decimal convictionScale = 1m) => new(
        Math.Round(s.Score, 3),
        s.Components.Where(c => c.Direction == s.Direction && c.Weight > 0).Select(c => c.Name).ToArray(),
        Math.Round(s.AtrPct, 4),
        Math.Round(s.SpreadBps, 2),
        s.Regime,
        aside?.FundingRate,
        aside?.AnchorLabel ?? "",
        nowUtc.Hour,
        nowUtc.DayOfWeek.ToString(),
        riskScale,
        Math.Round(s.AgreeingWeight, 3),
        Math.Round(s.OpposingWeight, 3),
        Math.Round(s.TriggerWeight, 3),
        Math.Round(s.Adx, 2),
        Math.Round(convictionScale, 3));
}

/// <summary>One closed trade, one JSON line. Field names are stable: analysis scripts depend on them.</summary>
public sealed class TradeRecord
{
    public long PositionId { get; set; }
    public string Market { get; set; } = "";
    public string Side { get; set; } = "";
    public DateTime OpenedUtc { get; set; }
    public DateTime ClosedUtc { get; set; }

    /// <summary>How OpenedUtc was established: venue, executions, saved, placed or first-seen.</summary>
    public string OpenedSource { get; set; } = "";
    public double HeldMinutes { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Size { get; set; }
    public decimal NetPnl { get; set; }
    public decimal GrossPnl { get; set; }
    public decimal Fees { get; set; }
    public decimal Funding { get; set; }
    public decimal NetPct { get; set; }
    /// <summary>Net PnL divided by the initial risk (size × stop distance). Null when the stop distance is unknown.</summary>
    public decimal? RMultiple { get; set; }
    public decimal MaxFavourableR { get; set; }
    public string ExitReason { get; set; } = "";
    public bool Adopted { get; set; }
    public bool PartialTaken { get; set; }
    public EntryContext? Entry { get; set; }
    /// <summary>Difference between the requested and confirmed average entry, signed adverse to the trade direction.</summary>
    public decimal? EntrySlippageBps { get; set; }

    public bool IsWin => NetPnl > 0;
}

/// <summary>
/// Append-only JSONL trade log plus the daily digest built from it. Writes are
/// best-effort: a journal failure must never stop a close from being handled.
/// </summary>
public sealed class TradeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string? _path;
    private readonly object _gate = new();

    public TradeJournal(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) { _path = null; return; }
        _path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    public bool Enabled => _path is not null;
    public string? FilePath => _path;

    public void Append(TradeRecord record)
    {
        if (_path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            lock (_gate) File.AppendAllText(_path, line, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"trade journal write failed ({_path}): {ex.Message}");
        }
    }

    /// <summary>All trades closed on the given UTC day.</summary>
    public IReadOnlyList<TradeRecord> ReadDay(DateOnly utcDay)
    {
        if (_path is null || !File.Exists(_path)) return Array.Empty<TradeRecord>();
        var result = new List<TradeRecord>();
        try
        {
            string[] lines;
            lock (_gate) lines = File.ReadAllLines(_path, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TradeRecord? rec;
                try { rec = JsonSerializer.Deserialize<TradeRecord>(line, JsonOptions); }
                catch { continue; }
                if (rec is null) continue;
                if (DateOnly.FromDateTime(rec.ClosedUtc) == utcDay) result.Add(rec);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"trade journal read failed ({_path}): {ex.Message}");
        }
        return result;
    }

    /// <summary>Reads all valid JSONL records. Malformed historical lines are skipped.</summary>
    public IReadOnlyList<TradeRecord> ReadAll()
    {
        if (_path is null || !File.Exists(_path)) return Array.Empty<TradeRecord>();
        var result = new List<TradeRecord>();
        try
        {
            string[] lines;
            lock (_gate) lines = File.ReadAllLines(_path, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<TradeRecord>(line, JsonOptions);
                    if (record is not null) result.Add(record);
                }
                catch { /* one damaged line must not hide a whole experiment */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"trade journal read failed ({_path}): {ex.Message}");
        }
        return result;
    }

    /// <summary>Compact, deterministic report for comparing frozen experiments offline.</summary>
    public static string FormatReport(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0) return "No closed trades in the journal.";

        var sb = new StringBuilder();
        sb.AppendLine($"Journal report · {trades.Count} closed trades · {trades.Min(t => t.ClosedUtc):yyyy-MM-dd} to {trades.Max(t => t.ClosedUtc):yyyy-MM-dd} UTC");
        AppendGroup("All", trades, sb);
        foreach (var group in trades.GroupBy(t => string.IsNullOrWhiteSpace(t.Entry?.ExperimentId) ? "legacy/unlabelled" : t.Entry.ExperimentId)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            AppendGroup($"Experiment {group.Key}", group.ToList(), sb);
        foreach (var group in trades.GroupBy(t => t.Market).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            AppendGroup($"Market {group.Key}", group.ToList(), sb);
        return sb.ToString().TrimEnd();
    }

    private static void AppendGroup(string label, IReadOnlyList<TradeRecord> trades, StringBuilder sb)
    {
        var wins = trades.Where(t => t.IsWin).ToList();
        var gains = wins.Sum(t => t.NetPnl);
        var losses = -trades.Where(t => !t.IsWin).Sum(t => t.NetPnl);
        var pf = losses > 0 ? gains / losses : (gains > 0 ? decimal.MaxValue : 0m);
        var avgR = trades.Where(t => t.RMultiple.HasValue).Select(t => t.RMultiple!.Value).DefaultIfEmpty().Average();
        var slips = trades.Where(t => t.EntrySlippageBps.HasValue).Select(t => t.EntrySlippageBps!.Value).ToList();
        sb.Append(CultureInfo.InvariantCulture,
            $"{label}: net {trades.Sum(t => t.NetPnl):+0.00;-0.00} USDT · {wins.Count}/{trades.Count} wins ({(double)wins.Count / trades.Count:P0}) · PF {(pf == decimal.MaxValue ? "∞" : pf.ToString("F2", CultureInfo.InvariantCulture))} · avg R {avgR:+0.00;-0.00}");
        if (slips.Count > 0) sb.Append(CultureInfo.InvariantCulture, $" · entry slip {slips.Average():+0.0;-0.0}bp");
        sb.AppendLine();
    }

    /// <summary>
    /// Telegram-sized summary of one day: result, fee share, exit-reason
    /// breakdown and the best/worst trade. Returns null when nothing traded.
    /// </summary>
    public static string? FormatDigest(DateOnly utcDay, IReadOnlyList<TradeRecord> trades, decimal? equity)
    {
        if (trades.Count == 0) return null;

        var wins = trades.Where(t => t.IsWin).ToList();
        var losses = trades.Where(t => !t.IsWin).ToList();
        var net = trades.Sum(t => t.NetPnl);
        var gross = trades.Sum(t => t.NetPnl + t.Funding + t.Fees); // PnL before trade fees and funding
        var fees = trades.Sum(t => t.Fees);
        var funding = trades.Sum(t => t.Funding);
        var grossWin = wins.Sum(t => t.NetPnl);
        var grossLoss = -losses.Sum(t => t.NetPnl);
        var pf = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? decimal.MaxValue : 0m);
        var avgHold = trades.Average(t => t.HeldMinutes);

        var sb = new StringBuilder();
        sb.Append(net >= 0 ? "📗 " : "📕 ");
        sb.Append(CultureInfo.InvariantCulture, $"Day {utcDay:yyyy-MM-dd} UTC · net {net:+0.00;-0.00} USDT\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"{trades.Count} trades · {wins.Count}W/{losses.Count}L ({(double)wins.Count / trades.Count:P0})");
        sb.Append(pf == decimal.MaxValue ? " · PF ∞\n" : string.Format(CultureInfo.InvariantCulture, " · PF {0:F2}\n", pf));

        var feeShare = gross > 0 ? fees / gross : (decimal?)null;
        sb.Append(CultureInfo.InvariantCulture, $"fees {fees:F2}");
        if (feeShare is { } fs) sb.Append(CultureInfo.InvariantCulture, $" ({fs:P0} of gross)");
        if (funding != 0) sb.Append(CultureInfo.InvariantCulture, $" · funding {funding:+0.00;-0.00}");
        sb.Append(CultureInfo.InvariantCulture, $" · avg hold {avgHold:F0}m\n");

        if (wins.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"avg win {wins.Average(t => t.NetPnl):+0.00}");
        if (losses.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"{(wins.Count > 0 ? " · " : "")}avg loss {losses.Average(t => t.NetPnl):0.00}");
        if (wins.Count > 0 || losses.Count > 0) sb.Append('\n');

        var reasons = trades
            .GroupBy(t => string.IsNullOrWhiteSpace(t.ExitReason) ? "unknown" : t.ExitReason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()} ({g.Sum(t => t.NetPnl):+0.00;-0.00})");
        sb.Append("exits: ").Append(string.Join(", ", reasons)).Append('\n');

        var best = trades.OrderByDescending(t => t.NetPnl).First();
        var worst = trades.OrderBy(t => t.NetPnl).First();
        sb.Append(CultureInfo.InvariantCulture,
            $"best {Short(best.Market)} {best.Side} {best.NetPnl:+0.00} · worst {Short(worst.Market)} {worst.Side} {worst.NetPnl:+0.00;-0.00}");

        var perMarket = trades.GroupBy(t => t.Market)
            .Select(g => $"{Short(g.Key)} {g.Sum(t => t.NetPnl):+0.00;-0.00}");
        if (trades.Select(t => t.Market).Distinct().Count() > 1)
            sb.Append("\nby market: ").Append(string.Join(", ", perMarket));

        if (equity is { } eq) sb.Append(CultureInfo.InvariantCulture, $"\nBalance {eq:F2} USDT");
        return sb.ToString();
    }

    private static string Short(string market)
    {
        var i = market.IndexOf('_');
        return i > 0 ? market[..i] : market;
    }
}
