using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Risk;
using TraderApp.Signals;

namespace TraderApp.Trading;

/// <summary>Per-position bookkeeping the exchange does not keep for us.</summary>
internal sealed class ManagedPosition : ITradeState
{
    public required long PositionId { get; init; }
    public required string Market { get; init; }
    public required bool IsLong { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal EntryAtr { get; init; }
    public required DateTime OpenedUtc { get; init; }

    /// <summary>The stop the bot enforces. Moves to break-even, then trails.</summary>
    public decimal StopPrice { get; set; }

    public decimal TakeProfitPrice { get; set; }

    /// <summary>Best price reached in our favour, for the trailing stop.</summary>
    public decimal HighWater { get; set; }

    public bool MovedToBreakEven { get; set; }

    /// <summary>Best R-multiple the trade ever reached. Distinguishes "never worked" from "worked then faded".</summary>
    public decimal MaxFavourableR { get; set; }
    public bool Adopted { get; init; }

    /// <summary>Set once a close has been requested, so it is never requested twice.</summary>
    public DateTime? CloseRequestedUtc { get; set; }

    /// <summary>Why the bot decided to close, kept for the close notification.</summary>
    public string LastExitReason { get; set; } = "";

    /// <summary>Size when first managed, so the journal's R-multiple uses the original risk.</summary>
    public decimal InitialSize { get; init; }

    /// <summary>Set once the partial take-profit has been placed (or ruled out for this trade).</summary>
    public bool PartialTaken { get; set; }

    /// <summary>What the signal looked like at entry. Null for adopted positions.</summary>
    public EntryContext? Entry { get; init; }
}

internal sealed record PendingEntry(long OrderId, string Market, bool IsLong, DateTime PlacedUtc);

/// <summary>A position that has closed but whose realised PnL has not been read back yet.</summary>
internal sealed record ClosedTrade(
    long PositionId, string Market, bool IsLong, DateTime ClosedUtc, string ExitReason, ManagedPosition State);

/// <summary>
/// The auto-trading loop.
///
/// Structural differences from the original timer-based version:
///  - a single sequential loop with an explicit delay, so ticks cannot overlap
///    and fire duplicate orders (the old `async void` timer callback could);
///  - every close is idempotent - a position that has already had a close
///    requested is skipped until the exchange confirms it is gone;
///  - exits use the close-position endpoint or reduceOnly orders, so an exit can
///    never accidentally open a position in the opposite direction;
///  - protective stops are also attached to the entry order (OTO), so the
///    position is protected even if this process is killed.
/// </summary>
public sealed class AutoTrader : IAsyncDisposable
{
    private readonly BotConfig _cfg;
    private readonly WhiteBitClient _client;
    private readonly SignalEngine _signals;
    private readonly StandAsideReader _standAside;
    private readonly RiskManager _risk;

    private readonly Dictionary<long, ManagedPosition> _managed = new();
    private readonly List<PendingEntry> _pending = new();
    private readonly List<ClosedTrade> _awaitingPnl = new();
    private IReadOnlyDictionary<string, MarketRules> _rules = new Dictionary<string, MarketRules>();
    private DateTime _rulesRefreshedUtc = DateTime.MinValue;

    private readonly bool _live;
    private readonly Notifications.TelegramNotifier? _telegram;
    private DateTime _lastHeartbeatUtc = DateTime.UtcNow;
    private DateTime _lastWatchdogUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastSizingRejectLog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastEntryVetoKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _closedEntryBarOpen = DateTime.MinValue;
    private int _tick;

    private decimal _lastEquity;
    private IReadOnlyList<Position> _lastPositions = Array.Empty<Position>();
    private DateTime _lastTickUtc;

    private readonly TradeJournal _journal;
    /// <summary>Signal context of the most recent entry order per market, attached to the position once it fills.</summary>
    private readonly Dictionary<string, (EntryContext Context, DateTime PlacedUtc)> _entryContext = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _lastDigestDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private bool _profitLockNotified;

    public AutoTrader(BotConfig cfg, WhiteBitClient client, Notifications.TelegramNotifier? telegram = null)
    {
        _cfg = cfg;
        _client = client;
        _telegram = telegram;
        _signals = new SignalEngine(cfg.Signals);
        _standAside = new StandAsideReader(cfg, client);
        _risk = new RiskManager(cfg.Risk, cfg.Trading);
        _live = !cfg.Trading.DryRun;
        _journal = new TradeJournal(cfg.Trading.TradeJournalPath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await BootstrapAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            if (_risk.Halted)
            {
                try
                {
                    var open = await _client.GetOpenPositionsAsync(null, ct);
                    if (open.Count > 0)
                    {
                        Log.Risk($"halt active — flattening {open.Count} open position(s)");
                        await FlattenAllAsync(open, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"could not flatten while halted: {ex.Message}");
                }

                Log.Risk($"HALTED - {_risk.HaltReason}");
                await ParkWhileHaltedAsync(ct);
                if (_risk.Halted || ct.IsCancellationRequested)
                    return;

                Log.Risk("resumed trading after UTC day rollover");
                _telegram?.Send($"▶️ Resumed · new UTC day\nBalance lock cleared — trading again.");
                continue;
            }

            while (!ct.IsCancellationRequested && !_risk.Halted)
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (ApiException ex)
                {
                    Log.Error($"API: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Error($"tick failed: {ex}");
                }

                try { await Task.Delay(_cfg.Trading.LoopIntervalMs, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Idle loop after a hard halt. Day-scoped locks (daily profit / session
    /// drawdown) auto-resume at the next UTC midnight. Hard halts
    /// (consecutive losses, margin call) stay until ClearHaltOnBoot.
    /// </summary>
    private async Task ParkWhileHaltedAsync(CancellationToken ct)
    {
        var dayScoped = _risk.HaltClearsOnUtcDay;
        if (dayScoped)
        {
            Log.Risk("parking until next UTC day (day-scoped lock) — or ClearHaltOnBoot to resume now");
            _telegram?.Send($"🛑 HALTED — parked until UTC midnight\n{_risk.HaltReason}\n" +
                            "Auto-resumes on the next UTC day. ClearHaltOnBoot to resume sooner.");
        }
        else
        {
            Log.Risk("parking process until Ctrl+C / SIGTERM — " +
                     "set TRADER_Risk__ClearHaltOnBoot=true to resume after restart");
            _telegram?.Send($"🛑 HALTED — parked\n{_risk.HaltReason}\n" +
                            "No new trades until ClearHaltOnBoot or state file cleared.");
        }

        var lastLog = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && _risk.Halted)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                var summary = await _client.GetCollateralSummaryAsync(ct);
                var equity = summary?.Equity ?? 0m;
                MaybeSendDailyDigest(equity);
                if (_risk.TickUtcDay(equity))
                {
                    Log.Risk("day-scoped halt cleared — leaving park");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"park equity check failed: {ex.Message}");
            }

            if (DateTime.UtcNow - lastLog < TimeSpan.FromMinutes(15)) continue;
            lastLog = DateTime.UtcNow;
            Log.Risk($"still parked (halted): {_risk.HaltReason}" +
                     (dayScoped ? " — waits for UTC day rollover" : ""));
            MaybePingWatchdog();
        }
    }

    // ── Startup ─────────────────────────────────────────────────────────

    private async Task BootstrapAsync(CancellationToken ct)
    {
        await RefreshMarketRulesAsync(ct);

        foreach (var market in _cfg.Trading.Markets)
        {
            if (!_rules.TryGetValue(market, out var r))
            {
                Log.Warn($"{market}: not present in /api/v4/public/markets - it will be skipped");
                continue;
            }
            Log.Info($"  {r}");
            if (!r.IsCollateral)
                Log.Warn($"{market}: isCollateral=false - collateral orders on it will be rejected");
            if (string.IsNullOrWhiteSpace(r.TickSize.ToString()) || r.TickSize <= 0)
                Log.Warn($"{market}: no tickSize from the exchange, falling back to moneyPrec");
        }

        CollateralSummary? summary;
        try
        {
            summary = await _client.GetCollateralSummaryAsync(ct);
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Unauthorized
                                          or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "WhiteBIT rejected the credentials. Check that the key and secret are correct, that the key has " +
                "trade permission for collateral markets, and that your current IP is on the key's allowlist. " +
                "Run with --signals-only to use the bot without credentials.", ex);
        }

        await ApplyAccountFeesAsync(ct);

        var equity = summary?.Equity ?? 0m;
        _risk.RestoreOrInitialise(equity);
        Log.Info($"collateral equity {equity:F2} | margin {summary?.Margin:F2} | free {summary?.FreeMargin:F2} | leverage {summary?.Leverage}x");

        if (_risk.Halted)
        {
            Log.Risk($"boot with active halt — will park after adopting open positions: {_risk.HaltReason}");
        }

        if (equity <= 0)
            Log.Warn("collateral equity is zero - fund the collateral account before enabling live trading");

        if (_live && summary is not null && summary.Leverage != _cfg.Trading.Leverage)
        {
            Log.Info($"setting account leverage {summary.Leverage}x -> {_cfg.Trading.Leverage}x");
            try { await _client.SetLeverageAsync(_cfg.Trading.Leverage, ct); }
            catch (ApiException ex) { Log.Warn($"could not set leverage: {ex.Message}"); }
        }

        _telegram?.Send(
            $"{(_live ? "🟢" : "⚪")} Started · {(_live ? "LIVE" : "DRY RUN")}" +
            $"{(_risk.Halted ? " · 🛑 HALTED" : "")}\n" +
            $"{BuildInfo.Display}\n" +
            $"Balance {equity:F2} USDT · {_cfg.Trading.Markets.Count} markets");

        // Adopt anything already open so it gets managed rather than ignored.
        var positions = await _client.GetOpenPositionsAsync(null, ct);
        foreach (var p in positions.Where(p => p.Market is not null))
            Log.Warn($"pre-existing position {p.Market} #{p.PositionId} size {p.Amount} @ {p.BasePrice} - will be adopted and managed");
    }

    /// <summary>
    /// Overwrite the public default fees with what this account actually pays.
    /// The fee sets the minimum move a scalp must capture, so using the market
    /// default when the account pays less would reject perfectly good setups.
    /// </summary>
    private async Task ApplyAccountFeesAsync(CancellationToken ct)
    {
        foreach (var market in _cfg.Trading.Markets)
        {
            if (!_rules.TryGetValue(market, out var r)) continue;
            try
            {
                var fee = await _client.GetMarketFeeAsync(market, ct);
                if (fee is null || !string.IsNullOrEmpty(fee.Error)) continue;

                var changed = fee.Maker != r.MakerFeePct || fee.Taker != r.TakerFeePct;
                r.ApplyAccountFees(fee.Maker, fee.Taker);
                if (changed)
                    Log.Info($"  {market}: account fee maker {fee.Maker}% / taker {fee.Taker}% " +
                             "(overrides the public default)");
            }
            catch (ApiException ex)
            {
                Log.Warn($"{market}: could not read account fee, using the public default - {ex.Message}");
            }
        }

        var sample = _cfg.Trading.Markets.FirstOrDefault(m => _rules.ContainsKey(m));
        if (sample is not null)
        {
            var r = _rules[sample];
            var rt = _risk.RoundTripFeePct(r);
            Log.Info($"round-trip cost {rt:F3}% of notional; a setup must target at least " +
                     $"{rt + _cfg.Risk.MinNetEdgePct:F3}% to be taken");
        }
    }

    private async Task RefreshMarketRulesAsync(CancellationToken ct)
    {
        _rules = await _client.GetMarketRulesAsync(ct);
        _rulesRefreshedUtc = DateTime.UtcNow;
        Log.Info($"loaded rules for {_rules.Count} markets");
    }

    // ── One decision cycle ──────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        _tick++;

        if (DateTime.UtcNow - _rulesRefreshedUtc > TimeSpan.FromHours(6))
            await RefreshMarketRulesAsync(ct);

        var summary = await _client.GetCollateralSummaryAsync(ct);
        var positions = await _client.GetOpenPositionsAsync(null, ct);
        var equity = summary?.Equity ?? 0m;

        // Snapshot for /status, so a Telegram command answers from memory
        // instead of firing extra private API calls on demand.
        _lastEquity = equity;
        _lastPositions = positions;
        _lastTickUtc = DateTime.UtcNow;

        // Digest before the day rolls inside CheckAccountHealth, so it reports
        // the day that just ended with the equity it ended on.
        MaybeSendDailyDigest(equity);

        if (_risk.CheckAccountHealth(equity, positions, out var haltReason))
        {
            Log.Risk($"account health check failed: {haltReason}");
            await FlattenAllAsync(positions, ct);
            return;
        }
        NoteProfitLock(equity);

        ReconcileManaged(positions);
        await ResolveClosedTradesAsync(equity, ct);

        // Consecutive-loss halt is set inside ResolveClosedTradesAsync; flatten
        // immediately rather than leaving orphans when the outer loop exits.
        if (_risk.Halted)
        {
            Log.Risk($"HALTED mid-tick - {_risk.HaltReason}");
            await FlattenAllAsync(positions, ct);
            return;
        }

        await ReconcilePendingAsync(ct);

        var openNotional = positions.Sum(p => p.AbsAmount * p.BasePrice);

        // 1. Manage what is already open. Exits always come before entries.
        foreach (var pos in positions)
        {
            if (pos.Market is null) continue;
            await ManagePositionAsync(pos, ct);
        }

        // 2. Scan every market, then take the single best setup. Filling
        //    slots in list order put ETH (first) on a stale trend and SOL
        //    (real trigger) in the same tick.
        await ConsiderEntriesAsync(equity, openNotional, positions, ct);

        if (ShouldStatusLog())
        {
            var scale = _risk.RiskScale();
            Log.Info($"equity {equity:F2} | open {positions.Count} | notional {openNotional:F2} | " +
                     $"pending {_pending.Count} | trades today {_risk.EntriesToday}/{_cfg.Risk.MaxTradesPerDay} | " +
                     $"loss streak {_risk.ConsecutiveLosses}" +
                     (scale < 1 ? $" | risk x{scale:0.##}{(_risk.ProfitLocked ? " (profit lock)" : "")}" : ""));
        }

        MaybeSendHeartbeat(equity, positions, openNotional);
        MaybePingWatchdog();
    }

    /// <summary>Telegram once when the day's profit lock switches the bot to reduced risk; reset on the new day.</summary>
    private void NoteProfitLock(decimal equity)
    {
        if (_risk.ProfitLocked && !_profitLockNotified)
        {
            _profitLockNotified = true;
            Log.Risk($"profit lock active — sizing at {_cfg.Risk.ProfitLockRiskScale:P0} for the rest of the UTC day");
            _telegram?.Send($"🔒 Profit lock · {_cfg.Risk.MaxDailyProfitPct}% day reached\n" +
                            $"Continuing at {_cfg.Risk.ProfitLockRiskScale:P0} risk until UTC midnight.\n" +
                            $"Balance {equity:F2} USDT");
        }
        else if (!_risk.ProfitLocked)
        {
            _profitLockNotified = false;
        }
    }

    /// <summary>
    /// At UTC midnight, summarise the day that just ended from the journal.
    /// One message per day; silent when nothing closed.
    /// </summary>
    private void MaybeSendDailyDigest(decimal equity)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _lastDigestDay) return;
        var ended = _lastDigestDay;
        _lastDigestDay = today;

        if (!_journal.Enabled) return;
        var trades = _journal.ReadDay(ended);
        var text = TradeJournal.FormatDigest(ended, trades, equity > 0 ? equity : null);
        if (text is null)
        {
            Log.Info($"daily digest {ended:yyyy-MM-dd}: no closed trades");
            return;
        }

        Log.Info($"daily digest {ended:yyyy-MM-dd}: {trades.Count} trades, net {trades.Sum(t => t.NetPnl):+0.00;-0.00} USDT");
        if (_cfg.Telegram.DailyDigest) _telegram?.Send(text);
    }

    /// <summary>
    /// Periodic "still alive" status. Silence from a trading bot is ambiguous -
    /// it could be running with no signals, or dead. This distinguishes them.
    /// </summary>
    private void MaybeSendHeartbeat(decimal equity, IReadOnlyList<Position> positions, decimal openNotional)
    {
        if (_telegram is null || _cfg.Telegram.HeartbeatMinutes <= 0) return;
        if (DateTime.UtcNow - _lastHeartbeatUtc < TimeSpan.FromMinutes(_cfg.Telegram.HeartbeatMinutes)) return;
        _lastHeartbeatUtc = DateTime.UtcNow;

        var pnlSinceStart = _risk.SessionStartEquity > 0 ? equity - _risk.SessionStartEquity : 0m;

        var sb = new System.Text.StringBuilder();
        sb.Append($"Balance {equity:F2} USDT  ({pnlSinceStart:+0.00;-0.00} session)\n");

        var real = positions.Where(p => p.AbsAmount > 0 && p.BasePrice > 0).ToList();
        if (real.Count == 0)
        {
            sb.Append("No open positions");
        }
        else
        {
            foreach (var p in real)
                sb.Append($"{Short(p.Market)} {(p.IsLong ? "long" : "short")} " +
                          $"{p.UnrealizedPnl:+0.00;-0.00} USDT\n");
        }

        _telegram.Send(sb.ToString().TrimEnd());
    }

    private void MaybePingWatchdog()
    {
        if (_telegram is null || string.IsNullOrWhiteSpace(_cfg.Telegram.WatchdogPingUrl)) return;
        var every = _cfg.Telegram.WatchdogPingMinutes > 0
            ? _cfg.Telegram.WatchdogPingMinutes
            : Math.Max(1, _cfg.Telegram.HeartbeatMinutes);
        if (_lastWatchdogUtc != DateTime.MinValue &&
            DateTime.UtcNow - _lastWatchdogUtc < TimeSpan.FromMinutes(every))
            return;
        _lastWatchdogUtc = DateTime.UtcNow;
        _telegram.PingWatchdog();
    }

    /// <summary>
    /// Telegram has no coloured text. Its HTML mode covers bold, italic, code,
    /// links and spoilers - there is no colour tag. A coloured emoji at the
    /// start of the line is the only way to get red/green, and it has the bonus
    /// of showing up in the chat-list preview.
    /// </summary>
    private static string FormatClose(ClosedTrade t, ClosedPnl rec, decimal pct, decimal equity)
    {
        var win = rec.NetPnl >= 0;
        var side = t.IsLong ? "long" : "short";

        var held = rec.Held;
        var heldText = held <= TimeSpan.Zero
            ? "?"
            : held.TotalHours >= 1
                ? $"{(int)held.TotalHours}h{held.Minutes:00}m"
                : $"{(int)held.TotalMinutes}m";

        var reason = string.IsNullOrWhiteSpace(t.ExitReason) ? "closed" : t.ExitReason;

        var sb = new System.Text.StringBuilder();
        sb.Append($"{(win ? "🟢" : "🔴")} {(win ? "WIN" : "LOSS")} · {Short(t.Market)} {side}\n");
        sb.Append($"P/L      {rec.NetPnl:+0.00;-0.00} USDT ({pct:+0.00;-0.00}%)\n");
        sb.Append($"Price    {rec.AvgEntryPrice} → {rec.AvgExitPrice}\n");
        sb.Append($"Held     {heldText} · {reason}\n");
        sb.Append($"Cost     fee {rec.TotalTradeFee:F3}");
        if (rec.TotalFunding != 0) sb.Append($" · funding {rec.TotalFunding:F3}");
        sb.Append($"\nBalance  {equity:F2} USDT");
        return sb.ToString();
    }

    /// <summary>Collapses a verbose exit reason to a short label for notifications.</summary>
    private static string Categorise(string reason) =>
        reason.StartsWith("stop hit") ? "stop"
        : reason.StartsWith("target hit") ? "target"
        : reason.StartsWith("never worked") ? "scratched"
        : reason.StartsWith("gave back") ? "gave back"
        : reason.StartsWith("time stop") ? "time stop"
        : reason.StartsWith("margin call") ? "margin call"
        : reason;

    /// <summary>ETH_USDT -> ETH. The quote currency is the same everywhere and just adds noise.</summary>
    // ── Telegram command surface ────────────────────────────────────────

    /// <summary>
    /// /pause - stop taking new setups. Deliberately does NOT flip DryRun:
    /// that would also stop the bot closing what it already holds, leaving an
    /// open position with only the wider exchange-side stop under it.
    /// </summary>
    public string Pause()
    {
        var open = _lastPositions.Count(p => p.AbsAmount > 0 && p.BasePrice > 0);
        if (!_risk.Pause())
            return "⏸ Already paused. /resume to re-enable entries.";

        return "⏸ Paused — no new entries.\n" +
               (open > 0
                   ? $"{open} open position(s) stay fully managed: stops, targets and the time stop still fire."
                   : "No open positions.") +
               "\nSurvives restarts. /resume to re-enable.";
    }

    /// <summary>/resume - allow new entries again.</summary>
    public string Resume()
    {
        if (!_risk.Resume())
            return "▶️ Not paused — entries are already enabled.";

        return "▶️ Resumed — taking new setups again.\n" + DescribeStatus();
    }

    /// <summary>
    /// Answers /status from the last tick's snapshot rather than issuing fresh
    /// private calls, so hammering the command cannot eat the API budget the
    /// trading loop depends on.
    /// </summary>
    public string DescribeStatus()
    {
        var age = _lastTickUtc == default ? "never" : $"{(DateTime.UtcNow - _lastTickUtc).TotalSeconds:F0}s ago";
        var real = _lastPositions.Where(p => p.AbsAmount > 0 && p.BasePrice > 0).ToList();
        var notional = real.Sum(p => p.AbsAmount * p.BasePrice);
        var session = _risk.SessionStartEquity > 0 ? _lastEquity - _risk.SessionStartEquity : 0m;

        var sb = new System.Text.StringBuilder();
        sb.Append($"{(_live ? "🟢" : "⚪")} {(_live ? "LIVE" : "DRY RUN")} · {BuildInfo.Display}\n");
        sb.Append($"Balance  {_lastEquity:F2} USDT ({session:+0.00;-0.00} session)\n");
        sb.Append($"Open     {real.Count}/{_cfg.Risk.MaxConcurrentPositions} · notional {notional:F2}\n");
        sb.Append($"Pending  {_pending.Count} · last tick {age}\n");
        if (_risk.Halted) sb.Append($"⛔ HALTED · {_risk.HaltReason}\n");
        else if (_risk.EntriesPaused)
        {
            var since = _risk.PausedAtUtc is { } p ? $" for {(DateTime.UtcNow - p).TotalMinutes:F0}m" : "";
            sb.Append($"⏸ PAUSED{since} · open positions still managed\n");
        }
        sb.Append($"Markets  {string.Join(" ", _cfg.Trading.Markets.Select(Short))}");
        return sb.ToString();
    }

    /// <summary>Open positions with their live unrealised PnL, for /positions.</summary>
    public string DescribePositions()
    {
        var real = _lastPositions.Where(p => p.AbsAmount > 0 && p.BasePrice > 0).ToList();
        if (real.Count == 0) return "No open positions.";

        var sb = new System.Text.StringBuilder();
        foreach (var p in real)
        {
            sb.Append($"{Short(p.Market)} {(p.IsLong ? "long" : "short")} {p.AbsAmount} @ {p.BasePrice}\n");
            if (_managed.TryGetValue(p.PositionId, out var m))
                sb.Append($"   stop {m.StopPrice} · tp {m.TakeProfitPrice} · best {m.MaxFavourableR:F2}R\n");
            sb.Append($"   uPnL {p.UnrealizedPnl:+0.0000;-0.0000} USDT\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Short(string? market) =>
        market is null ? "?" : market.Replace("_USDT", "", StringComparison.OrdinalIgnoreCase);

    // ── Entries ─────────────────────────────────────────────────────────

    private sealed record ScannedEntry(
        TradeSignal Signal, OrderBook Book, MarketRules Rules, decimal EntryPrice, SizingResult Sizing,
        StandAsideSnapshot? Aside);

    private async Task ConsiderEntriesAsync(
        decimal equity, decimal openNotional, IReadOnlyList<Position> positions, CancellationToken ct)
    {
        var reserved = _pending.Select(p => p.Market).ToList();
        var slots = _cfg.Risk.MaxConcurrentPositions - positions.Count - _pending.Count;
        if (slots <= 0)
        {
            if (ShouldStatusLog())
            {
                foreach (var market in _cfg.Trading.Markets)
                    Log.Info($"{market}: no entry - {positions.Count + _pending.Count} open/pending, limit {_cfg.Risk.MaxConcurrentPositions}");
            }
            return;
        }

        var newClosedBar = await NoteClosedEntryBarAsync(ct);

        var candidates = new List<ScannedEntry>();
        var scans = new List<Task<ScannedEntry?>>();
        foreach (var market in _cfg.Trading.Markets)
        {
            if (ct.IsCancellationRequested) break;
            if (!_risk.CanOpenNewPosition(market, positions, out var blockReason, _pending.Count, reserved))
            {
                if (newClosedBar) Log.Info($"{market}: no entry - {blockReason}");
                continue;
            }

            if (!newClosedBar &&
                _lastEntryVetoKey.TryGetValue(market, out var prior) &&
                prior != "micro")
                continue;

            scans.Add(ScanEntrySafeAsync(market, equity, openNotional, newClosedBar, ct));
        }

        foreach (var scanned in await Task.WhenAll(scans))
        {
            if (scanned is not null) candidates.Add(scanned);
        }

        var take = Math.Max(1, Math.Min(slots, _cfg.Trading.MaxEntriesPerTick));
        foreach (var chosen in candidates
                     .OrderByDescending(c => c.Signal.Score)
                     .ThenBy(c => c.Signal.SpreadBps)
                     .Take(take))
        {
            if (!_risk.CanOpenNewPosition(chosen.Signal.Market, positions, out _, _pending.Count, reserved))
                continue;
            if (!AllowsDirection(chosen.Signal.Direction == Signals.Direction.Long, positions))
            {
                Log.Info($"{chosen.Signal.Market}: skipped - already at the same-direction cap " +
                         $"({_cfg.Risk.MaxSameDirectionPositions})");
                continue;
            }
            await PlaceEntryAsync(chosen, ct);
            reserved.Add(chosen.Signal.Market);
        }
    }

    private bool AllowsDirection(bool isLong, IReadOnlyList<Position> positions)
    {
        var cap = _cfg.Risk.MaxSameDirectionPositions;
        if (cap <= 0) return true;
        var open = positions.Count(p => p.AbsAmount > 0 && p.IsLong == isLong);
        var pending = _pending.Count(p => p.IsLong == isLong);
        return open + pending < cap;
    }

    /// <summary>
    /// Signals are closed-bar. Re-scoring the same 15m candle every 2s only
    /// reprints the same vetoes and lets the book flicker a fake "almost".
    /// </summary>
    private async Task<bool> NoteClosedEntryBarAsync(CancellationToken ct)
    {
        var probe = _cfg.Trading.Markets.FirstOrDefault();
        if (probe is null) return true;
        var candles = await _client.GetKlinesAsync(
            probe, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
        if (candles.Count == 0) return true;
        var open = candles[^1].OpenTime;
        if (open == _closedEntryBarOpen) return false;
        _closedEntryBarOpen = open;
        _lastEntryVetoKey.Clear();
        Log.Info($"new {_cfg.Trading.EntryInterval} bar {open:HH:mm} UTC — scanning entries");
        return true;
    }

    private async Task<ScannedEntry?> ScanEntrySafeAsync(
        string market, decimal equity, decimal openNotional, bool logBlocked, CancellationToken ct)
    {
        try
        {
            return await ScanEntryAsync(market, equity, openNotional, logBlocked, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"{market}: entry evaluation failed - {ex.Message}");
            return null;
        }
    }

    private async Task<ScannedEntry?> ScanEntryAsync(
        string market, decimal equity, decimal openNotional, bool logBlocked, CancellationToken ct)
    {
        if (!_rules.TryGetValue(market, out var rules)) return null;

        var depth = Math.Max(_cfg.Signals.OrderBookDepth, 20);
        var bookTask = _client.GetOrderBookAsync(market, depth, ct);
        var entryTask = _client.GetKlinesAsync(market, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
        var regimeTask = _client.GetKlinesAsync(market, _cfg.Trading.RegimeInterval, _cfg.Trading.CandleLookback, ct);
        var asideTask = _standAside.LoadAsync(market, ct);
        await Task.WhenAll(bookTask, entryTask, regimeTask, asideTask);

        var book = await bookTask;
        if (book is null) return null;
        var entry = await entryTask;
        var regime = await regimeTask;

        var aside = await asideTask;
        var signal = _signals.Evaluate(market, entry, regime, book, aside);

        if (!signal.IsActionable)
        {
            var key = signal.OnlyMicrostructureVetoes ? "micro" : signal.Explain();
            var changed = !_lastEntryVetoKey.TryGetValue(market, out var prev) || prev != key;
            _lastEntryVetoKey[market] = key;
            if (logBlocked || changed) Log.Info($"{market}: {signal.Explain()}");
            return null;
        }

        _lastEntryVetoKey[market] = "ok";

        var isLong = signal.Direction == Signals.Direction.Long;

        // Two entry styles, chosen by Trading.PostOnlyEntries:
        //
        //  post-only : rest at the NEAR touch (buy at bid, sell at ask). Pays no
        //              spread, but only fills if the market comes to us - and on
        //              WhiteBIT maker and taker are both 0.1% on these markets,
        //              so resting earns no fee discount to compensate.
        //  crossing  : quote the FAR touch (buy at ask, sell at bid) so the order
        //              fills immediately against resting size. Still a limit
        //              order, so slippage is capped at the spread - and the
        //              MaxSpreadBps veto already caps that slippage.
        //
        // Rounding must follow the intent: a resting buy rounds down to stay
        // passive, a crossing buy rounds up so it still reaches the ask.
        decimal reference, entryPrice;
        if (_cfg.Trading.PostOnlyEntries)
        {
            reference = isLong ? book.BestBid!.Value : book.BestAsk!.Value;
            entryPrice = isLong ? rules.FloorPrice(reference) : rules.CeilPrice(reference);
        }
        else
        {
            reference = isLong ? book.BestAsk!.Value : book.BestBid!.Value;
            entryPrice = isLong ? rules.CeilPrice(reference) : rules.FloorPrice(reference);
        }

        var sizing = _risk.Size(isLong, entryPrice, signal.Atr, equity, openNotional, rules);
        if (!sizing.Allowed)
        {
            if (ShouldLogSizingReject(market))
                Log.Info($"{market}: {signal.Direction} setup rejected by sizing - {sizing.Reason}");
            return null;
        }

        return new ScannedEntry(signal, book, rules, entryPrice, sizing, aside);
    }

    private bool ShouldLogSizingReject(string market)
    {
        if (_lastSizingRejectLog.TryGetValue(market, out var last) &&
            DateTime.UtcNow - last < TimeSpan.FromMinutes(2))
            return false;
        _lastSizingRejectLog[market] = DateTime.UtcNow;
        return true;
    }

    private async Task PlaceEntryAsync(ScannedEntry scanned, CancellationToken ct)
    {
        var signal = scanned.Signal;
        var market = signal.Market;
        var rules = scanned.Rules;
        var sizing = scanned.Sizing;
        var isLong = signal.Direction == Signals.Direction.Long;

        var riskQuote = sizing.Amount * Math.Abs(scanned.EntryPrice - sizing.StopPrice);
        Log.Trade(
            $"{market} {signal.Direction} @ {rules.FormatPriceRaw(scanned.EntryPrice)} " +
            $"qty {rules.FormatAmount(sizing.Amount)} " +
            $"stop {rules.FormatPriceRaw(sizing.StopPrice)} " +
            $"tp {rules.FormatPriceRaw(sizing.TakeProfitPrice)} " +
            $"hardStop {rules.FormatPriceRaw(sizing.HardStopPrice)} | " +
            $"risk {riskQuote:F2} | {signal.Explain()}");

        foreach (var c in signal.Components.Where(c => c.Weight > 0))
            Log.Info($"    {c.Name,-10} {c.Direction,-5} w={c.Weight:F2}  {c.Detail}");

        // Remembered per market and attached when the fill shows up as a
        // position, so the journal can grade the trade against its setup.
        _entryContext[market] = (EntryContext.From(signal, scanned.Aside, _risk.RiskScale(), DateTime.UtcNow), DateTime.UtcNow);

        if (!_live)
        {
            Log.Info($"{market}: DRY RUN - no order sent");
            return;
        }

        var payload = new CollateralLimitOrderPayload
        {
            Market = market,
            Side = isLong ? "buy" : "sell",
            Amount = rules.FormatAmount(sizing.Amount),
            Price = rules.FormatPriceRaw(scanned.EntryPrice),
            PostOnly = _cfg.Trading.PostOnlyEntries,
            ClientOrderId = $"ta-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            StopLoss = rules.FormatPriceRaw(sizing.HardStopPrice),
            TakeProfit = rules.FormatPriceRaw(sizing.TakeProfitPrice),
            Stp = "co"
        };

        try
        {
            var order = await _client.PlaceCollateralLimitAsync(payload, ct);
            if (order is null) return;

            _pending.Add(new PendingEntry(order.OrderId, market, isLong, DateTime.UtcNow));
            Log.Trade($"{market}: entry order #{order.OrderId} placed ({order.Status}), left {order.Left}");
        }
        catch (ApiException ex)
        {
            Log.Warn($"{market}: entry rejected - {ex.Message}");
        }
    }

    private async Task ReconcilePendingAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;

        var timeout = TimeSpan.FromSeconds(_cfg.Trading.EntryOrderTimeoutSeconds);
        foreach (var p in _pending.ToList())
        {
            IReadOnlyList<OrderResponse> open;
            try { open = await _client.GetOpenOrdersAsync(p.Market, ct); }
            catch (ApiException ex) { Log.Warn($"{p.Market}: could not list open orders - {ex.Message}"); continue; }

            var still = open.FirstOrDefault(o => o.OrderId == p.OrderId);
            if (still is null)
            {
                // Gone from the book without us cancelling it, so it filled. This
                // is the point at which a trade has actually happened, and the
                // only point at which the cooldown and rate limit should apply.
                _risk.RecordEntry(p.Market);
                Log.Trade($"{p.Market}: entry #{p.OrderId} filled");
                if (_cfg.Telegram.SendTrades)
                    _telegram?.Send($"Opened {Short(p.Market)} {(p.IsLong ? "long" : "short")}");
                _pending.Remove(p);
                continue;
            }

            if (DateTime.UtcNow - p.PlacedUtc <= timeout) continue;

            try
            {
                await _client.CancelOrderAsync(p.Market, p.OrderId, ct);
                Log.Trade($"{p.Market}: entry #{p.OrderId} not filled within {timeout.TotalSeconds:F0}s - " +
                          "cancelled (no cooldown consumed)");
                _pending.Remove(p);
            }
            catch (ApiException ex)
            {
                // Leave it in the list and retry next tick rather than losing
                // track of a live order, which is what the old code did.
                Log.Warn($"{p.Market}: cancel of #{p.OrderId} failed - {ex.Message}");
            }
        }
    }

    // ── Position management ─────────────────────────────────────────────

    private void ReconcileManaged(IReadOnlyList<Position> positions)
    {
        var live = positions.Select(p => p.PositionId).ToHashSet();

        foreach (var id in _managed.Keys.ToList())
        {
            if (live.Contains(id)) continue;
            var m = _managed[id];
            _managed.Remove(id);
            Log.Trade($"{m.Market}: position #{id} is gone from the exchange - dropped from management");

            // The realised result is not in this response; it has to be read
            // back from the closed-PnL endpoint, which lags the close by a
            // moment. Queue it and resolve over the next few ticks.
            _awaitingPnl.Add(new ClosedTrade(id, m.Market, m.IsLong, DateTime.UtcNow, m.LastExitReason, m));
        }
    }

    /// <summary>
    /// Resolves the realised profit or loss of recently closed positions and
    /// reports it with the resulting balance. Inferring PnL from equity deltas
    /// would be wrong whenever more than one position is open, so the exchange's
    /// own closed-PnL record is used instead.
    /// </summary>
    private async Task ResolveClosedTradesAsync(decimal equity, CancellationToken ct)
    {
        if (_awaitingPnl.Count == 0) return;

        IReadOnlyList<ClosedPnl> closed;
        try { closed = await _client.GetClosedPnlAsync(20, ct); }
        catch (ApiException ex) { Log.Warn($"could not read closed PnL - {ex.Message}"); return; }

        foreach (var t in _awaitingPnl.ToList())
        {
            var rec = closed.FirstOrDefault(c => c.PositionId == t.PositionId);

            if (rec is null)
            {
                // Give the exchange a little time before giving up on the number.
                if (DateTime.UtcNow - t.ClosedUtc < TimeSpan.FromMinutes(2)) continue;

                Log.Warn($"{t.Market}: no closed-PnL record for #{t.PositionId} after 2 minutes");
                if (_cfg.Telegram.SendTrades)
                    _telegram?.Send($"Closed {Short(t.Market)} {(t.IsLong ? "long" : "short")}\n" +
                                    $"P/L unavailable\n" +
                                    $"Balance {equity:F2} USDT");
                _awaitingPnl.Remove(t);
                continue;
            }

            var notional = rec.AvgEntryPrice * rec.ClosedSize;
            var pct = notional > 0 ? rec.NetPnl / notional * 100m : 0m;

            _risk.RecordTradeResult(profitable: rec.NetPnl > 0, netPnl: rec.NetPnl);
            Log.Trade($"{t.Market} #{t.PositionId} closed: net {rec.NetPnl:+0.0000;-0.0000} USDT " +
                      $"({pct:+0.00;-0.00}%), fees {rec.TotalTradeFee:F4}, funding {rec.TotalFunding:F4}");

            _journal.Append(BuildRecord(t, rec, pct));

            if (_cfg.Telegram.SendTrades)
                _telegram?.Send(FormatClose(t, rec, pct, equity));

            _awaitingPnl.Remove(t);
        }
    }

    private TradeRecord BuildRecord(ClosedTrade t, ClosedPnl rec, decimal pct)
    {
        var m = t.State;
        var stopDistance = m.EntryAtr * _cfg.Trading.StopLossAtrMult;
        var initialRisk = m.InitialSize * stopDistance;
        var opened = rec.OpenDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(rec.OpenDate).UtcDateTime : m.OpenedUtc;
        var closed = rec.CloseDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(rec.CloseDate).UtcDateTime : t.ClosedUtc;

        return new TradeRecord
        {
            PositionId = t.PositionId,
            Market = t.Market,
            Side = t.IsLong ? "long" : "short",
            OpenedUtc = opened,
            ClosedUtc = closed,
            HeldMinutes = Math.Round((closed - opened).TotalMinutes, 1),
            EntryPrice = rec.AvgEntryPrice,
            ExitPrice = rec.AvgExitPrice,
            Size = rec.ClosedSize,
            NetPnl = rec.NetPnl,
            GrossPnl = rec.GrossPnl,
            Fees = rec.TotalTradeFee,
            Funding = rec.TotalFunding,
            NetPct = Math.Round(pct, 4),
            RMultiple = initialRisk > 0 ? Math.Round(rec.NetPnl / initialRisk, 3) : null,
            MaxFavourableR = Math.Round(m.MaxFavourableR, 3),
            ExitReason = string.IsNullOrWhiteSpace(t.ExitReason) ? "exchange" : t.ExitReason,
            Adopted = m.Adopted,
            PartialTaken = m.PartialTaken,
            Entry = m.Entry,
        };
    }

    private async Task ManagePositionAsync(Position pos, CancellationToken ct)
    {
        if (pos.Market is null || !_rules.TryGetValue(pos.Market, out var rules)) return;

        // WhiteBIT reports a position row as soon as an order exists, with a null
        // basePrice and zero size until it fills. There is nothing to manage yet,
        // and adopting it would derive stop and target from a zero entry price.
        // It still counts towards the position limits, so it is filtered here
        // rather than out of the list.
        if (pos.AbsAmount <= 0 || pos.BasePrice <= 0)
        {
            if (ShouldStatusLog())
                Log.Info($"{pos.Market} #{pos.PositionId}: not filled yet (size {pos.Amount}, base {pos.BasePrice}) - nothing to manage");
            return;
        }

        var book = await _client.GetOrderBookAsync(pos.Market, 5, ct);
        var mark = book?.Mid ?? pos.BasePrice;
        if (mark <= 0) return;

        if (!_managed.TryGetValue(pos.PositionId, out var m))
        {
            m = await AdoptAsync(pos, rules, ct);
            if (m is null) return;
            _managed[pos.PositionId] = m;
        }

        // A close is in flight; do not stack another one on top of it.
        if (m.CloseRequestedUtc is { } requested)
        {
            if (DateTime.UtcNow - requested < TimeSpan.FromSeconds(30)) return;
            Log.Warn($"{pos.Market}: close requested {(DateTime.UtcNow - requested).TotalSeconds:F0}s ago and the position is still open - retrying");
            m.CloseRequestedUtc = null;
        }

        var moveInFavour = (mark - m.EntryPrice) * (m.IsLong ? 1m : -1m);
        var r = m.EntryAtr * _cfg.Trading.StopLossAtrMult;
        var rMultiple = r > 0 ? moveInFavour / r : 0m;
        var floating = pos.FloatingPnl(mark);
        var roundTrip = _risk.RoundTripFeePct(rules);

        // Shared with the backtester — live must honour BreakEvenEnabled etc.
        var stopChange = ExitRules.UpdateStops(
            m, mark, _cfg.Trading, roundTrip,
            (price, isBuy) => rules.SnapPriceForSide(price, isBuy));
        if (stopChange is not null)
            Log.Trade($"{pos.Market}: {stopChange}");

        if (await TryPartialTakeAsync(pos, m, rules, mark, rMultiple, roundTrip, ct))
            return; // let the exchange report the reduced size before deciding anything else

        var regimeAgainst = await ShouldExitAgainstRegimeAsync(pos, m, rMultiple, ct);
        var exitReason = ExitRules.Decide(
            m, mark, DateTime.UtcNow, _cfg.Trading, roundTrip,
            pos.InTrouble, regimeAgainst);

        if (exitReason is null)
        {
            if (ShouldStatusLog())
                Log.Info($"{pos.Market} #{pos.PositionId} {(m.IsLong ? "LONG" : "SHORT")} " +
                         $"{pos.AbsAmount} @ {pos.BasePrice} | mark {mark} | uPnL {floating:F4} " +
                         $"({rMultiple:F2}R) | stop {m.StopPrice} | tp {m.TakeProfitPrice}");
            return;
        }

        Log.Trade($"{pos.Market} #{pos.PositionId}: closing - {exitReason} | uPnL {floating:F4}");

        // Stash why, so the close notification can say more than "closed". The
        // exchange's closed-PnL record has the prices and timing but not the
        // reason - that only exists here.
        m.LastExitReason = Categorise(exitReason);

        if (!_live)
        {
            Log.Info($"{pos.Market}: DRY RUN - close not sent");
            _managed.Remove(pos.PositionId);
            return;
        }

        m.CloseRequestedUtc = DateTime.UtcNow;
        try
        {
            // close-position also cancels the attached OTO protection, which a
            // bare reduceOnly order would leave orphaned on the book.
            await _client.ClosePositionAsync(pos.PositionId, pos.Market, pos.PositionSide, ct);
            Log.Trade($"{pos.Market} #{pos.PositionId}: close requested");
        }
        catch (ApiException ex)
        {
            Log.Error($"{pos.Market} #{pos.PositionId}: close failed - {ex.Message}");
            m.CloseRequestedUtc = null;
        }
    }

    /// <summary>
    /// Bank part of the trade at PartialTakeAtR with a reduce-only market
    /// order and put the rest behind a break-even+fees stop. Returns true when
    /// an order was sent this tick. Both the closed part and the remainder
    /// must clear the market minimums, otherwise the partial is skipped for
    /// this trade and the normal exits apply.
    /// </summary>
    private async Task<bool> TryPartialTakeAsync(
        Position pos, ManagedPosition m, MarketRules rules, decimal mark, decimal rMultiple,
        decimal roundTrip, CancellationToken ct)
    {
        var cfg = _cfg.Trading;
        if (cfg.PartialTakeAtR <= 0 || m.PartialTaken || rMultiple < cfg.PartialTakeAtR) return false;

        var closeAmount = rules.FloorAmount(pos.AbsAmount * cfg.PartialTakeFraction);
        var remainder = pos.AbsAmount - closeAmount;
        string? why = null;
        if (!rules.IsTradable(closeAmount, mark, out var whyClose)) why = $"closing part: {whyClose}";
        else if (!rules.IsTradable(remainder, mark, out var whyRest)) why = $"remainder: {whyRest}";
        if (why is not null)
        {
            m.PartialTaken = true;
            Log.Info($"{pos.Market} #{pos.PositionId}: partial take skipped at {rMultiple:F2}R - {why}");
            return false;
        }

        var be = ExitRules.BreakEvenPrice(m, roundTrip, (price, isBuy) => rules.SnapPriceForSide(price, isBuy));
        Log.Trade($"{pos.Market} #{pos.PositionId}: partial take {rules.FormatAmount(closeAmount)} of " +
                  $"{pos.AbsAmount} at {rMultiple:F2}R (>= {cfg.PartialTakeAtR}R), stop -> {be}");

        if (!_live)
        {
            m.PartialTaken = true;
            ApplyPartialStop(m, be, mark);
            Log.Info($"{pos.Market}: DRY RUN - partial close not sent");
            return false;
        }

        try
        {
            var order = await _client.PlaceCollateralMarketAsync(new CollateralMarketOrderPayload
            {
                Market = pos.Market!,
                Side = m.IsLong ? "sell" : "buy",
                Amount = rules.FormatAmount(closeAmount),
                ReduceOnly = true,
                PositionSide = pos.PositionSide,
                ClientOrderId = $"tp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Stp = "co"
            }, ct);

            m.PartialTaken = true;
            ApplyPartialStop(m, be, mark);
            Log.Trade($"{pos.Market} #{pos.PositionId}: partial close order #{order?.OrderId} ({order?.Status})");
            if (_cfg.Telegram.SendTrades)
                _telegram?.Send($"✂️ Partial {Short(pos.Market)} {(m.IsLong ? "long" : "short")} · " +
                                $"{cfg.PartialTakeFraction:P0} off at {rMultiple:F1}R, rest at break-even");
            return true;
        }
        catch (ApiException ex)
        {
            // Do not retry every 2s against a rejecting endpoint; the normal
            // target / trail still manage the full size.
            m.PartialTaken = true;
            Log.Warn($"{pos.Market} #{pos.PositionId}: partial close rejected - {ex.Message}");
            return false;
        }
    }

    private static void ApplyPartialStop(ManagedPosition m, decimal be, decimal mark)
    {
        var improves = (m.IsLong && be > m.StopPrice) || (!m.IsLong && be < m.StopPrice);
        var behindMark = (m.IsLong && be < mark) || (!m.IsLong && be > mark);
        if (improves && behindMark)
        {
            m.StopPrice = be;
            m.MovedToBreakEven = true;
        }
    }

    /// <summary>
    /// Build management state for a position we did not open in this session
    /// (a restart, or one opened by hand). Stops are derived from current ATR.
    /// </summary>
    private async Task<ManagedPosition?> AdoptAsync(Position pos, MarketRules rules, CancellationToken ct)
    {
        var candles = await _client.GetKlinesAsync(
            pos.Market!, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
        if (candles.Count < _cfg.Signals.AtrPeriod + 2)
        {
            Log.Warn($"{pos.Market}: cannot adopt #{pos.PositionId}, not enough candles for ATR");
            return null;
        }

        var atrSeries = Ta.Atr(candles, _cfg.Signals.AtrPeriod);
        var atr = Ta.IsUsable(atrSeries[^1]) ? (decimal)atrSeries[^1] : 0m;
        if (atr <= 0)
        {
            Log.Warn($"{pos.Market}: cannot adopt #{pos.PositionId}, ATR unavailable");
            return null;
        }

        var isLong = pos.IsLong;
        var sign = isLong ? 1m : -1m;
        var entry = pos.BasePrice;

        if (entry <= 0)
        {
            Log.Warn($"{pos.Market}: cannot adopt #{pos.PositionId}, no usable base price");
            return null;
        }

        // A fill of our own entry order shows up here too. If this session
        // placed an order on this market a few minutes ago, the position is
        // ours and carries that signal's context; otherwise it is adopted.
        EntryContext? context = null;
        if (_entryContext.TryGetValue(pos.Market!, out var ctx) &&
            DateTime.UtcNow - ctx.PlacedUtc < TimeSpan.FromMinutes(15))
        {
            context = ctx.Context;
            _entryContext.Remove(pos.Market!);
        }

        var m = new ManagedPosition
        {
            PositionId = pos.PositionId,
            Market = pos.Market!,
            IsLong = isLong,
            EntryPrice = entry,
            EntryAtr = atr,
            OpenedUtc = pos.OpenedAtUtc == default ? DateTime.UtcNow : pos.OpenedAtUtc,
            Adopted = context is null,
            InitialSize = pos.AbsAmount,
            Entry = context,
            HighWater = entry,
            StopPrice = rules.SnapPriceForSide(entry - sign * atr * _cfg.Trading.StopLossAtrMult, isBuy: !isLong),
            TakeProfitPrice = rules.SnapPriceForSide(entry + sign * atr * _cfg.Trading.TakeProfitAtrMult, isBuy: isLong)
        };

        Log.Trade($"{pos.Market}: {(context is null ? "adopted" : "managing")} #{pos.PositionId} {(isLong ? "LONG" : "SHORT")} " +
                  $"entry {entry} stop {m.StopPrice} tp {m.TakeProfitPrice} (ATR {atr})");
        return m;
    }

    private bool ShouldStatusLog()
    {
        var every = Math.Max(1, _cfg.Trading.StatusLogEveryTicks);
        return _tick % every == 1;
    }

    /// <summary>
    /// A long held into a decided down regime (the adopted ETH in the last run)
    /// should not wait for a take-profit that the higher timeframe is working against.
    /// Only fires when the trade is already underwater so a working winner is left alone.
    /// </summary>
    private async Task<bool> ShouldExitAgainstRegimeAsync(
        Position pos, ManagedPosition m, decimal rMultiple, CancellationToken ct)
    {
        if (rMultiple >= -0.5m) return false;
        var age = DateTime.UtcNow - m.OpenedUtc;
        if (age < TimeSpan.FromMinutes(90)) return false;
        if (_tick % 12 != 1) return false;

        try
        {
            var depth = Math.Max(_cfg.Signals.OrderBookDepth, 20);
            var bookTask = _client.GetOrderBookAsync(pos.Market!, depth, ct);
            var entryTask = _client.GetKlinesAsync(pos.Market!, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
            var regimeTask = _client.GetKlinesAsync(pos.Market!, _cfg.Trading.RegimeInterval, _cfg.Trading.CandleLookback, ct);
            await Task.WhenAll(bookTask, entryTask, regimeTask);
            var book = await bookTask;
            if (book is null) return false;
            var signal = _signals.Evaluate(pos.Market!, await entryTask, await regimeTask, book);
            if (signal.RegimeDirection == Signals.Direction.None) return false;

            var holding = m.IsLong ? Signals.Direction.Long : Signals.Direction.Short;
            return signal.RegimeDirection != holding;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"{pos.Market}: regime check failed - {ex.Message}");
            return false;
        }
    }

    // ── Shutdown / emergency ────────────────────────────────────────────

    private async Task FlattenAllAsync(IReadOnlyList<Position> positions, CancellationToken ct)
    {
        foreach (var p in positions.Where(p => p.Market is not null))
        {
            if (!_live)
            {
                Log.Info($"{p.Market}: DRY RUN - would flatten #{p.PositionId}");
                continue;
            }
            try
            {
                await _client.ClosePositionAsync(p.PositionId, p.Market!, p.PositionSide, ct);
                Log.Risk($"{p.Market}: flattened #{p.PositionId}");
            }
            catch (ApiException ex)
            {
                Log.Error($"{p.Market}: flatten failed - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// On shutdown, cancel resting entry orders. Open positions are left alone
    /// deliberately - they still carry the exchange-side stop attached at entry,
    /// and force-closing on Ctrl+C would turn a routine restart into a realised loss.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_live || _pending.Count == 0) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var p in _pending.ToList())
        {
            try
            {
                await _client.CancelOrderAsync(p.Market, p.OrderId, cts.Token);
                Log.Info($"{p.Market}: cancelled resting entry #{p.OrderId} on shutdown");
            }
            catch (Exception ex)
            {
                Log.Warn($"{p.Market}: could not cancel #{p.OrderId} on shutdown - {ex.Message}");
            }
        }

        if (_managed.Count > 0)
            Log.Warn($"{_managed.Count} position(s) left open. They keep their exchange-side stop-loss, " +
                     "but no trailing or time stop until the bot is restarted.");
    }
}
