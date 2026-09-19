using System.Text.Json;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Risk;

/// <summary>
/// Durable session risk state on disk. The container filesystem is ephemeral
/// unless a volume is mounted, but even without a volume this helps across
/// in-place process restarts; with a volume at SessionStatePath it survives
/// redeploys too. Pair with park-on-halt so Railway does not restart after a
/// deliberate session halt.
/// </summary>
public sealed class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly bool _enabled;

    public SessionStateStore(RiskConfig cfg)
    {
        _enabled = cfg.PersistSessionState;
        var configured = string.IsNullOrWhiteSpace(cfg.SessionStatePath)
            ? System.IO.Path.Combine("data", "session-state.json")
            : cfg.SessionStatePath;
        _path = System.IO.Path.IsPathRooted(configured)
            ? configured
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, configured));
    }

    public string FilePath => _path;
    public bool Enabled => _enabled;

    public SessionState? TryLoad()
    {
        if (!_enabled) return null;
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not load session state from {_path}: {ex.Message}");
            return null;
        }
    }

    public void Save(SessionState state)
    {
        if (!_enabled) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not save session state to {_path}: {ex.Message}");
        }
    }

    public void Clear()
    {
        if (!_enabled) return;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not clear session state at {_path}: {ex.Message}");
        }
    }
}
