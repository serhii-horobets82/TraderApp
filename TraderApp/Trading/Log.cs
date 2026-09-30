namespace TraderApp.Trading;

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>
    /// Optional sink for important lines, wired to Telegram at startup.
    /// Receives (level, message). Must not throw and must not log through this
    /// class, or it would recurse - use <see cref="Diag"/> instead.
    /// </summary>
    private static Action<string, string>? _notify;

    public static void ToFile(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public static void SetNotifier(Action<string, string>? notify) => _notify = notify;

    /// <summary>
    /// Venue tag applied to every line written on the current async flow.
    ///
    /// AsyncLocal is the point: each venue runs as its own task, and the value
    /// flows across every await and into nested parallel work inside that
    /// venue. Tagging at the call site instead would mean touching ~80 Log
    /// calls and silently losing the tag on every new one.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentVenue = new();

    /// <summary>Tag everything logged inside the returned scope with [label].</summary>
    public static IDisposable VenueScope(string label)
    {
        var previous = CurrentVenue.Value;
        CurrentVenue.Value = label;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentVenue.Value = previous;
    }

    public static void Info(string msg) => Write("INFO ", msg, ConsoleColor.Gray, notify: false);
    public static void Trade(string msg) => Write("TRADE", msg, ConsoleColor.Cyan, notify: true);
    public static void Warn(string msg) => Write("WARN ", msg, ConsoleColor.Yellow, notify: true);
    public static void Error(string msg) => Write("ERROR", msg, ConsoleColor.Red, notify: true);
    public static void Risk(string msg) => Write("RISK ", msg, ConsoleColor.Magenta, notify: true);

    /// <summary>
    /// Console and file only, never forwarded to the notifier. Used by the
    /// notifier itself to report its own failures without recursing.
    /// </summary>
    public static void Diag(string msg) => Write("DIAG ", msg, ConsoleColor.DarkGray, notify: false);

    private static void Write(string level, string msg, ConsoleColor colour, bool notify)
    {
        // Shared code (Telegram listener, startup, shutdown) runs outside any
        // venue scope and is left untagged, which is correct - it belongs to no
        // venue.
        var venue = CurrentVenue.Value;
        var tagged = venue is null ? msg : $"[{venue}] {msg}";

        var line = $"{DateTime.UtcNow:HH:mm:ss} {level} {tagged}";
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
            _file?.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {level} {tagged}");
        }

        if (!notify) return;

        // Outside the lock: the notifier only enqueues, but a sink must never be
        // able to deadlock logging.
        try { _notify?.Invoke(level.Trim(), tagged); }
        catch { /* a broken notifier must not break logging */ }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
        }
    }
}
