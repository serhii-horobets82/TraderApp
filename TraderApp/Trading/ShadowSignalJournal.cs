using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraderApp.Signals;

namespace TraderApp.Trading;

/// <summary>
/// Append-only observations of closed-bar decisions. Unlike the trade journal,
/// this contains signals the bot intentionally did not trade. It is strictly
/// diagnostic: writing a record cannot affect the order path.
/// </summary>
public sealed class ShadowSignalJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string? _path;
    private readonly object _gate = new();

    public ShadowSignalJournal(string? configuredPath, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(configuredPath)) return;
        _path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    public void Append(string experimentId, TradeSignal signal, decimal? entryPrice,
        decimal? plannedRiskQuote, bool wouldEnter, string decision)
    {
        if (_path is null) return;
        try
        {
            var record = new ShadowSignalRecord
            {
                ObservedUtc = DateTime.UtcNow,
                ExperimentId = experimentId,
                Market = signal.Market,
                Direction = signal.Direction.ToString(),
                Score = Math.Round(signal.Score, 4),
                AtrPct = Math.Round(signal.AtrPct, 4),
                SpreadBps = Math.Round(signal.SpreadBps, 2),
                Regime = signal.Regime,
                WouldEnter = wouldEnter,
                Decision = decision,
                IntendedEntryPrice = entryPrice,
                PlannedRiskQuote = plannedRiskQuote,
                Components = signal.Components.Where(c => c.Weight > 0)
                    .Select(c => $"{c.Name}:{c.Direction}:{c.Weight:F2}").ToArray()
            };
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_gate)
                File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"shadow signal journal write failed ({_path}): {ex.Message}");
        }
    }
}

public sealed class ShadowSignalRecord
{
    public DateTime ObservedUtc { get; set; }
    public string ExperimentId { get; set; } = "";
    public string Market { get; set; } = "";
    public string Direction { get; set; } = "";
    public double Score { get; set; }
    public decimal AtrPct { get; set; }
    public decimal SpreadBps { get; set; }
    public string Regime { get; set; } = "";
    public bool WouldEnter { get; set; }
    public string Decision { get; set; } = "";
    public decimal? IntendedEntryPrice { get; set; }
    public decimal? PlannedRiskQuote { get; set; }
    public IReadOnlyList<string> Components { get; set; } = Array.Empty<string>();
}
