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
        var line = $"{DateTime.UtcNow:HH:mm:ss} {level} {msg}";
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
            _file?.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {level} {msg}");
        }

        if (!notify) return;

        // Outside the lock: the notifier only enqueues, but a sink must never be
        // able to deadlock logging.
        try { _notify?.Invoke(level.Trim(), msg); }
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
