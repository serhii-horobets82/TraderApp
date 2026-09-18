using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Notifications;

/// <summary>
/// Pushes important events to a Telegram chat.
///
/// Design rules, in order of importance:
///  1. Telegram must never affect trading. Sending is queued and handled on a
///     background task; a slow, throttled or dead Telegram cannot block, slow or
///     fault the decision loop.
///  2. Never throw into the caller. Failures are logged locally and dropped.
///  3. Never recurse. Failures are written with <see cref="Log.Diag"/>, which
///     deliberately does not feed back into the notifier.
///  4. Bound the queue. Under a flood the oldest messages are dropped and the
///     drop is reported, rather than growing memory without limit.
/// </summary>
public sealed class TelegramNotifier : IAsyncDisposable
{
    private const int TelegramMaxMessageChars = 4096;

    private readonly TelegramConfig _cfg;
    private readonly HttpClient _http;
    private readonly Channel<string> _queue;
    private readonly Task _worker;
    private readonly CancellationTokenSource _cts = new();
    private int _dropped;

    public bool IsConfigured => _cfg.Enabled
                                && !string.IsNullOrWhiteSpace(_cfg.BotToken)
                                && !string.IsNullOrWhiteSpace(_cfg.ChatId);

    public TelegramNotifier(TelegramConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _worker = IsConfigured ? Task.Run(() => PumpAsync(_cts.Token)) : Task.CompletedTask;
    }

    /// <summary>Queue a message. Returns immediately; never throws.</summary>
    public void Send(string message)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(message)) return;
        if (!_queue.Writer.TryWrite(message)) Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Send immediately and wait for Telegram to accept it. Used on shutdown and
    /// fatal errors: the queued worker may already be cancelled, and a queued
    /// "bot down" that never leaves the process is useless.
    /// </summary>
    public async Task SendNowAsync(string message, TimeSpan? timeout = null)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(message)) return;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await SendBatchAsync(message, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Diag($"[telegram] urgent send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Dead-man's switch ping (healthchecks.io or any URL that expects GET).
    /// Fire-and-forget so a hung watchdog cannot stall the trading loop.
    /// </summary>
    public void PingWatchdog()
    {
        var url = _cfg.WatchdogPingUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = PingWatchdogAsync(url, fail: false);
    }

    /// <summary>Tell the watchdog this process is going away, so it alerts now instead of after the grace period.</summary>
    public Task SignalWatchdogDownAsync()
    {
        var url = _cfg.WatchdogPingUrl;
        if (string.IsNullOrWhiteSpace(url)) return Task.CompletedTask;
        var failUrl = url.TrimEnd('/') + "/fail";
        return PingWatchdogAsync(failUrl, fail: true);
    }

    private async Task PingWatchdogAsync(string url, bool fail)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode)
                Log.Diag($"[watchdog] ping {(fail ? "fail" : "ok")} HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Diag($"[watchdog] ping failed: {ex.Message}");
        }
    }

    /// <summary>Routes a log line to Telegram if its level is enabled.</summary>
    public void SendFromLog(string level, string message)
    {
        // Opens, closes and the balance are sent explicitly by AutoTrader as
        // short summaries. Raw log lines are the firehose - every signal, order
        // id and stop move - so they only go out in Verbose mode.
        var send = level switch
        {
            "TRADE" => _cfg.Verbose,
            "WARN" => _cfg.Verbose,
            "RISK" => _cfg.SendRisk,
            "ERROR" => _cfg.SendErrors,
            _ => false
        };
        if (!send) return;

        var prefix = level switch
        {
            "RISK" => "! ",
            "ERROR" => "x ",
            _ => ""
        };
        Send($"{prefix}{message}");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var batch = new List<string>();

        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                var length = 0;

                // Drain whatever is queued into a single message: a burst of
                // events during one tick arrives as one notification instead of
                // ten, which also keeps us clear of Telegram's per-chat limits.
                while (batch.Count < 25 && _queue.Reader.TryRead(out var item))
                {
                    if (length + item.Length + 1 > TelegramMaxMessageChars - 200) break;
                    batch.Add(item);
                    length += item.Length + 1;
                }

                if (batch.Count == 0) continue;

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    batch.Add($"({dropped} earlier message(s) dropped - queue full)");

                await SendBatchAsync(string.Join("\n", batch), ct);
                await Task.Delay(_cfg.MinSendGapMs, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Diag($"[telegram] notifier stopped unexpectedly: {ex.Message}");
        }
    }

    private async Task SendBatchAsync(string text, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_cfg.BotToken}/sendMessage";
            using var resp = await _http.PostAsJsonAsync(url, new
            {
                chat_id = _cfg.ChatId,
                text,
                disable_web_page_preview = true
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Diag($"[telegram] send failed HTTP {(int)resp.StatusCode}: {Trim(body)}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Diag($"[telegram] send failed: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";

    /// <summary>
    /// A chat id is a (possibly negative) integer, or an @channelusername.
    /// Worth checking: a mistyped or mis-pasted value otherwise fails silently
    /// at send time, long after the mistake was made.
    /// </summary>
    public static bool LooksLikeChatId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        id = id.Trim();
        if (id.StartsWith('@')) return id.Length > 1 && !id.Contains(' ');
        return long.TryParse(id, out _);
    }

    // ── Setup helper ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the token and, when no chat id is configured, lists the chats
    /// that have messaged the bot so the id can be copied into config.
    /// </summary>
    public static async Task<int> RunSetupCheckAsync(TelegramConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BotToken))
        {
            Console.WriteLine("No bot token. Set TRADER_Telegram__BotToken or TELEGRAM_BOT_TOKEN.");
            Console.WriteLine("Create a bot by messaging @BotFather on Telegram; it replies with the token.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var me = await http.GetAsync($"https://api.telegram.org/bot{cfg.BotToken}/getMe", ct);
        var meBody = await me.Content.ReadAsStringAsync(ct);
        if (!me.IsSuccessStatusCode)
        {
            Console.WriteLine($"Token rejected by Telegram (HTTP {(int)me.StatusCode}): {Trim(meBody)}");
            return 1;
        }

        using (var doc = JsonDocument.Parse(meBody))
        {
            var name = doc.RootElement.GetProperty("result").GetProperty("username").GetString();
            Console.WriteLine($"Token OK - bot is @{name}");
        }

        if (string.IsNullOrWhiteSpace(cfg.ChatId))
        {
            Console.WriteLine("\nNo ChatId configured. Send any message to the bot, then re-run this.");
            var upd = await http.GetAsync($"https://api.telegram.org/bot{cfg.BotToken}/getUpdates", ct);
            var updBody = await upd.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(updBody);
            if (!doc.RootElement.TryGetProperty("result", out var results) || results.GetArrayLength() == 0)
            {
                Console.WriteLine("No messages seen yet, so no chat id to report.");
                return 1;
            }

            Console.WriteLine("\nChats that have messaged this bot:");
            var seen = new HashSet<string>();
            foreach (var u in results.EnumerateArray())
            {
                foreach (var key in new[] { "message", "channel_post", "my_chat_member" })
                {
                    if (!u.TryGetProperty(key, out var m) || !m.TryGetProperty("chat", out var chat)) continue;
                    var id = chat.GetProperty("id").GetRawText();
                    if (!seen.Add(id)) continue;
                    var title = chat.TryGetProperty("title", out var t) ? t.GetString()
                        : chat.TryGetProperty("username", out var un) ? un.GetString()
                        : chat.TryGetProperty("first_name", out var fn) ? fn.GetString() : "(unnamed)";
                    Console.WriteLine($"  ChatId {id}   {title}");
                }
            }
            Console.WriteLine("\nPut the id in appsettings.json as Telegram.ChatId, or set TELEGRAM_CHAT_ID.");
            return 1;
        }

        if (!LooksLikeChatId(cfg.ChatId))
        {
            Console.WriteLine($"\nChatId is not a valid Telegram chat id: \"{cfg.ChatId}\"");
            Console.WriteLine("It must be a number (e.g. 123456789, or -1001234567890 for a group/channel),");
            Console.WriteLine("or an @channelusername. Telegram.ChatId in appsettings.json is used unless it");
            Console.WriteLine("is empty, in which case the TELEGRAM_CHAT_ID environment variable is read.");
            return 1;
        }

        // Send directly rather than through the queued notifier: the notifier is
        // a no-op until Telegram.Enabled is true, and this check has to work
        // BEFORE that is turned on. Reporting success without confirming the
        // send is worse than not checking at all.
        Console.WriteLine($"\nSending a test message to chat {cfg.ChatId}...");
        using var resp = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{cfg.BotToken}/sendMessage",
            new
            {
                chat_id = cfg.ChatId,
                text = $"TraderApp test message - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                disable_web_page_preview = true
            }, ct);

        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            Console.WriteLine("Delivered. Check Telegram.");
            Console.WriteLine("Now set Telegram.Enabled to true in appsettings.json to turn notifications on.");
            return 0;
        }

        Console.WriteLine($"Telegram rejected the send (HTTP {(int)resp.StatusCode}): {Trim(respBody)}");
        if (respBody.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("\"chat not found\" usually means the id is wrong, or you have not sent the bot " +
                              "a message yet. Open the bot in Telegram, press Start, then re-run this.");
        else if (respBody.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("You have blocked this bot in Telegram. Unblock it and re-run this.");
        return 1;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // Give the worker a moment to flush what is already queued, but never
        // let shutdown hang on a network call.
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* timeout or already faulted */ }

        await _cts.CancelAsync();
        _cts.Dispose();
        _http.Dispose();
    }
}
