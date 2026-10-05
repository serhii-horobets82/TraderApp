using System.Text;
using System.Text.Json;

namespace TraderApp.Trading;

/// <summary>
/// Everything the bot tracks about an open position that the exchange does not.
/// Persisted so a restart resumes the same trade - above all its real opening
/// time - rather than re-adopting it as brand new or, worse, as ancient.
/// </summary>
public sealed class SavedPosition
{
    public string Market { get; set; } = "";
    public bool IsLong { get; set; }
    public DateTime OpenedUtc { get; set; }

    /// <summary>Where OpenedUtc came from: venue, executions, saved, placed or first-seen.</summary>
    public string OpenedSource { get; set; } = "";

    public decimal EntryPrice { get; set; }
    public decimal EntryAtr { get; set; }
    public decimal InitialSize { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public decimal HighWater { get; set; }
    public bool MovedToBreakEven { get; set; }
    public decimal MaxFavourableR { get; set; }
    public bool PartialTaken { get; set; }
    public bool Adopted { get; set; }
    public EntryContext? Entry { get; set; }

    public static string KeyOf(string market, bool isLong) =>
        $"{market.ToUpperInvariant()}|{(isLong ? "L" : "S")}";

    public string Key => KeyOf(Market, IsLong);

    /// <summary>
    /// Whether this record describes the position the exchange reports now. A
    /// record left over from an earlier trade on the same symbol must not
    /// lend its opening time to a new one: the entry price has to agree and the
    /// size cannot have grown (the bot never adds to a position).
    /// </summary>
    public bool Matches(decimal basePrice, decimal absSize)
    {
        if (EntryPrice <= 0 || basePrice <= 0) return false;
        var drift = Math.Abs(EntryPrice - basePrice) / basePrice;
        return drift <= 0.002m && absSize <= InitialSize * 1.001m;
    }
}

/// <summary>Plausibility rules for an opening time before it is trusted for the time stop.</summary>
public static class OpenTimeRules
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns the candidate if it can describe a position that exists now, else
    /// null. A position cannot have opened in the future, nor before the entry
    /// order that created it was placed - a candidate that says otherwise is
    /// the symbol's stale first-ever timestamp, not this trade.
    /// </summary>
    public static DateTime? Verify(DateTime? candidate, DateTime nowUtc, DateTime? entryOrderPlacedUtc)
    {
        if (candidate is not { } c || c.Year < 2020) return null;
        if (c > nowUtc + FutureTolerance) return null;
        if (entryOrderPlacedUtc is { } placed && c < placed - ClockSkew) return null;
        return c > nowUtc ? nowUtc : c;
    }
}

/// <summary>
/// JSON file holding the state of every managed position. Writes are atomic
/// (temp + rename) and best-effort: a failure is logged but must never stop
/// the trading loop.
/// </summary>
public sealed class PositionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string? _path;
    private string _lastWritten = "";

    public PositionStateStore(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) { _path = null; return; }
        _path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    public bool Enabled => _path is not null;
    public string? FilePath => _path;

    public IReadOnlyList<SavedPosition> Load()
    {
        if (_path is null || !File.Exists(_path)) return Array.Empty<SavedPosition>();
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<SavedPosition>>(json, JsonOptions);
            _lastWritten = json;
            return list ?? new List<SavedPosition>();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not load position state from {_path}: {ex.Message}");
            return Array.Empty<SavedPosition>();
        }
    }

    /// <summary>Writes the snapshot when it differs from what is already on disk. Returns true if it wrote.</summary>
    public bool Save(IReadOnlyList<SavedPosition> positions)
    {
        if (_path is null) return false;
        try
        {
            var json = JsonSerializer.Serialize(positions, JsonOptions);
            if (json == _lastWritten) return false;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
            _lastWritten = json;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not save position state to {_path}: {ex.Message}");
            return false;
        }
    }
}
