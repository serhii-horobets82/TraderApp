using System.Text.Json;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Notifications;

/// <summary>
/// Listens for commands sent to the Telegram bot and dispatches them.
///
/// Security: a Telegram bot receives messages from anyone who finds its handle,
/// so every update is checked against the configured ChatId and anything from
/// another chat is dropped. Without that check a stranger could restart - or
/// with a richer command set, interfere with - a bot trading real money.
///
/// The listener uses long polling. That conflicts with any other process
/// polling the same token (Telegram returns 409), so only one instance of the
/// bot may have commands enabled at a time.
/// </summary>
public sealed class TelegramCommandListener : IAsyncDisposable
{
    private readonly TelegramConfig _cfg;
    private readonly Action<string> _reply;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private long _offset;
    private bool _warnedForeignChat;

    public TelegramCommandListener(TelegramConfig cfg, Action<string> reply)
    {
        _cfg = cfg;
        _reply = reply;
        // Long polling holds the request open, so the timeout must exceed the
        // poll window or every poll would cancel itself.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(PollSeconds + 20) };
    }

    private const int PollSeconds = 30;

    public bool IsConfigured => _cfg.Enabled
                                && _cfg.CommandsEnabled
                                && !string.IsNullOrWhiteSpace(_cfg.BotToken)
                                && !string.IsNullOrWhiteSpace(_cfg.ChatId);

    /// <summary>Register a handler. The returned string is sent back as the reply.</summary>
    public void On(string command, Func<CancellationToken, Task<string>> handler) =>
        _handlers[command] = handler;

    public void On(string command, Func<string> handler) =>
        _handlers[command] = _ => Task.FromResult(handler());

    public async Task RunAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;

        await DrainBacklogAsync(ct);
        Log.Info($"Telegram commands on: {string.Join(" ", _handlers.Keys.OrderBy(k => k))}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a polling failure take the trader down with it.
                Log.Diag($"[telegram] command poll failed: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Skip anything already queued before startup.
    ///
    /// Telegram keeps undelivered updates for 24h. Without this, a "/restart"
    /// sent before the bot came up would be replayed on boot - and since the
    /// handler restarts the process, that is an infinite restart loop.
    /// </summary>
    private async Task DrainBacklogAsync(CancellationToken ct)
    {
        try
        {
            var url = Url($"getUpdates?offset=-1&timeout=0");
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var results)) return;

            foreach (var u in results.EnumerateArray())
                if (u.TryGetProperty("update_id", out var id))
                    _offset = Math.Max(_offset, id.GetInt64() + 1);

            if (_offset > 0) Log.Info($"[telegram] skipped {results.GetArrayLength()} queued update(s) from before startup");
        }
        catch (Exception ex)
        {
            Log.Diag($"[telegram] could not drain backlog: {ex.Message}");
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // allowed_updates is a JSON array, so it has to be escaped rather than
        // pasted raw into the query string.
        var allowed = Uri.EscapeDataString("[\"message\"]");
        var url = Url($"getUpdates?offset={_offset}&timeout={PollSeconds}&allowed_updates={allowed}");
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 409)
                Log.Warn("[telegram] another instance is polling this bot token - commands disabled here until it stops");
            else
                Log.Diag($"[telegram] getUpdates HTTP {(int)resp.StatusCode}");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("result", out var results)) return;

        foreach (var update in results.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var idEl))
                _offset = Math.Max(_offset, idEl.GetInt64() + 1);

            if (!update.TryGetProperty("message", out var msg)) continue;
            if (!msg.TryGetProperty("chat", out var chat)) continue;
            if (!msg.TryGetProperty("text", out var textEl)) continue;

            var chatId = chat.TryGetProperty("id", out var cid) ? cid.GetRawText() : "";
            if (!string.Equals(chatId, _cfg.ChatId, StringComparison.Ordinal))
            {
                if (!_warnedForeignChat)
                {
                    _warnedForeignChat = true;
                    Log.Warn($"[telegram] ignoring command from unauthorised chat {chatId}");
                }
                continue;
            }

            await DispatchAsync(textEl.GetString() ?? "", ct);
        }
    }

    private async Task DispatchAsync(string text, CancellationToken ct)
    {
        // "/restart@MyBot extra args" -> "/restart"
        var word = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var at = word.IndexOf('@');
        if (at > 0) word = word[..at];
        if (!word.StartsWith('/')) return;

        if (!_handlers.TryGetValue(word, out var handler))
        {
            _reply($"Unknown command {word}. Try /help");
            return;
        }

        Log.Info($"[telegram] command {word}");
        try
        {
            var reply = await handler(ct);
            if (!string.IsNullOrWhiteSpace(reply)) _reply(reply);
        }
        catch (Exception ex)
        {
            Log.Error($"[telegram] command {word} failed: {ex.Message}");
            _reply($"{word} failed: {ex.Message}");
        }
    }

    public IReadOnlyCollection<string> Commands => _handlers.Keys;

    private string Url(string path) => $"https://api.telegram.org/bot{_cfg.BotToken}/{path}";

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
