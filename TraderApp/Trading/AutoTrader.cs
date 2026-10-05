using System.Net;
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

    /// <summary>Which source the opening time came from (venue, executions, saved, placed, first-seen).</summary>
    public string OpenedSource { get; init; } = "";

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

    /// <summary>Consecutive failed close attempts, for backoff.</summary>
    public int CloseFailures { get; set; }

    /// <summary>Earliest time to try closing again. Stops a rejected close retrying every tick.</summary>
    public DateTime? CloseRetryAfterUtc { get; set; }

    /// <summary>Set once the venue has been alerted that closes are being rejected.</summary>
    public bool CloseBlockedNotified { get; set; }

    /// <summary>Last Telegram reminder that the exit is still blocked; reminders are hourly, not per retry.</summary>
    public DateTime? CloseBlockedRemindedUtc { get; set; }

    /// <summary>Why the bot decided to close, kept for the close notification.</summary>
    public string LastExitReason { get; set; } = "";

    /// <summary>Size when first managed, so the journal's R-multiple uses the original risk.</summary>
    public decimal InitialSize { get; init; }

    /// <summary>Set once the partial take-profit has been placed (or ruled out for this trade).</summary>
    public bool PartialTaken { get; set; }

    /// <summary>What the signal looked like at entry. Null for adopted positions.</summary>
    public EntryContext? Entry { get; init; }
}

/// <summary>An entry order resting on the book. Ids are strings end to end: Bybit's are UUIDs.</summary>
internal sealed class PendingEntry
{
    public PendingEntry(string orderId, string? clientOrderId, string market, bool isLong, DateTime placedUtc)
    {
        OrderId = orderId;
        ClientOrderId = clientOrderId;
        Market = market;
        IsLong = isLong;
        PlacedUtc = placedUtc;
    }

    public string OrderId { get; set; }
    public string? ClientOrderId { get; }
    public string Market { get; }
    public bool IsLong { get; }
    public DateTime PlacedUtc { get; }

    /// <summary>Consecutive failed cancels; drives backoff so a stuck order is not retried every tick.</summary>
    public int CancelFailures { get; set; }
    public DateTime? NextCancelUtc { get; set; }

    /// <summary>First tick the order was absent from the open book, while its outcome is still unconfirmed.</summary>
    public DateTime? MissingSinceUtc { get; set; }

    public string Label => string.IsNullOrEmpty(OrderId) ? $"[{ClientOrderId}]" : OrderId;

    public bool Is(OrderResponse o) =>
        (!string.IsNullOrEmpty(OrderId) && string.Equals(o.OrderId, OrderId, StringComparison.Ordinal)) ||
        (!string.IsNullOrEmpty(ClientOrderId) &&
         string.Equals(o.ClientOrderId, ClientOrderId, StringComparison.Ordinal));
}

/// <summary>A position that has closed but whose realised PnL has not been read back yet.</summary>
internal sealed record ClosedTrade(
    long PositionId, string Market, bool IsLong, DateTime ClosedUtc, string ExitReason, ManagedPosition State);

/// <summary>A settled passive-entry attempt used to monitor fill quality.</summary>
internal sealed record EntryOutcomeSample(DateTime SettledUtc, bool Filled);

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
    private readonly IExchangeClient _client;
    private readonly SignalEngine _signals;
    private readonly StandAsideReader _standAside;
    private readonly RiskManager _risk;
    private readonly string _venueLabel;

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
    private int _barProbeFailures;
    private int _tick;

    private decimal _lastEquity;
    private IReadOnlyList<Position> _lastPositions = Array.Empty<Position>();
    private DateTime _lastTickUtc;

    private readonly TradeJournal _journal;
    private readonly PositionStateStore _positionStore;
    private readonly Dictionary<string, SavedPosition> _saved = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPositionSaveUtc = DateTime.MinValue;
    private string _lastPositionKeys = "";

    /// <summary>Closed-PnL records already booked to a trade, so one record is never counted twice.</summary>
    private readonly HashSet<string> _bookedClosedPnl = new(StringComparer.Ordinal);

    private DateTime _entriesBlockedUntilUtc = DateTime.MinValue;
    private DateTime _lastEntryBlockedStatusLogUtc = DateTime.MinValue;
    private bool _entryBlockNotified;
    private static readonly TimeSpan EntryBlockReprobe = TimeSpan.FromHours(6);
    /// <summary>Signal context of the most recent entry order per market, attached to the position once it fills.</summary>
    private readonly Dictionary<string, (EntryContext Context, DateTime PlacedUtc)> _entryContext = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _lastDigestDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private bool _profitLockNotified;
    private decimal? _flatEquityRef;
    private DateTime _lastFlatEquityWarnUtc = DateTime.MinValue;
    private readonly object _postOnlyOutcomeLock = new();
    private readonly Queue<EntryOutcomeSample> _postOnlyOutcomes = new();
    private DateTime _lastPostOnlyFillWarningUtc = DateTime.MinValue;
    private DateTime _lastIdleStatusUtc = DateTime.MinValue;

    public AutoTrader(BotConfig cfg, IExchangeClient client, Notifications.TelegramNotifier? telegram = null)
    {
        _cfg = cfg;
        _client = client;
        _venueLabel = client.ExchangeId;
        _telegram = telegram;
        _signals = new SignalEngine(cfg.Signals);
        _standAside = new StandAsideReader(cfg, client);
        _risk = new RiskManager(cfg.Risk, cfg.Trading);
        _live = !cfg.Trading.DryRun;
        _journal = new TradeJournal(cfg.Trading.TradeJournalPath);
        _positionStore = new PositionStateStore(cfg.Trading.PositionStatePath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Tags every line this venue writes, including nested parallel scans.
        using var venueScope = Log.VenueScope(_venueLabel);

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
                var summary = await _client.GetAccountSummaryAsync(ct);
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
                Log.Warn($"{market}: not listed by the exchange - it will be skipped");
                continue;
            }
            Log.Info($"  {r}");
            if (!r.IsCollateral)
                Log.Warn($"{market}: isCollateral=false - orders on it will be rejected");
            if (string.IsNullOrWhiteSpace(r.TickSize.ToString()) || r.TickSize <= 0)
                Log.Warn($"{market}: no tickSize from the exchange, falling back to moneyPrec");
        }

        CollateralSummary? summary;
        try
        {
            summary = await _client.GetAccountSummaryAsync(ct);
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Unauthorized
                                          or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"{_venueLabel} rejected the credentials. Check that the key and secret are correct, that the key has " +
                "trade permission, and that your current IP is on the key's allowlist. " +
                "Run with --signals-only to use the bot without credentials.", ex);
        }

        await ApplyAccountFeesAsync(ct);
        await CheckCollateralDenominationAsync(ct);

        var equity = summary?.Equity ?? 0m;
        _risk.RestoreOrInitialise(equity);
        Log.Info($"equity {equity:F2} | margin {summary?.Margin:F2} | free {summary?.FreeMargin:F2} | leverage {summary?.Leverage}x");

        if (_risk.Halted)
        {
            Log.Risk($"boot with active halt — will park after adopting open positions: {_risk.HaltReason}");
        }

        if (equity <= 0)
            Log.Warn($"equity is zero - fund the account before enabling live trading");

        if (_live && summary is not null && summary.Leverage != 0 && summary.Leverage != _cfg.Trading.Leverage)
        {
            Log.Info($"setting account leverage {summary.Leverage}x -> {_cfg.Trading.Leverage}x");
            try { await _client.SetLeverageAsync(_cfg.Trading.Leverage, ct); }
            catch (ApiException ex) { Log.Warn($"could not set leverage: {ex.Message}"); }
        }
        else if (_live && summary is not null && summary.Leverage == 0)
        {
            // Bybit reports leverage per position, not on the wallet summary,
            // so the summary line above always reads 0x. Say what was applied -
            // silence here makes "leverage 0x" look like leverage was never set.
            try
            {
                await _client.SetLeverageAsync(_cfg.Trading.Leverage, ct);
                Log.Info($"leverage {_cfg.Trading.Leverage}x applied to " +
                         $"{_cfg.Trading.Markets.Count} symbol(s) — the 0x above is the wallet " +
                         "summary, which does not carry per-symbol leverage on this venue");
            }
            catch (ApiException ex) { Log.Warn($"could not set leverage: {ex.Message}"); }
        }

        _telegram?.Send(
            $"{(_live ? "🟢" : "⚪")} Started · {_venueLabel} · {(_live ? "LIVE" : "DRY RUN")}" +
            $"{(_risk.Halted ? " · 🛑 HALTED" : "")}\n" +
            $"{BuildInfo.Display}\n" +
            $"Balance {equity:F2} USDT · {_cfg.Trading.Markets.Count} markets");

        // Adopt anything already open so it gets managed rather than ignored.
        var positions = await _client.GetOpenPositionsAsync(null, ct);
        foreach (var p in positions.Where(p => p.Market is not null))
            Log.Warn($"pre-existing position {p.Market} #{p.PositionId} size {p.Amount} @ {p.BasePrice} - will be adopted and managed");

        LoadSavedPositions();
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

    /// <summary>
    /// The drawdown / profit locks read collateral equity. If the collateral
    /// holds anything but the quote currency, its price moves are counted as
    /// trading PnL: a BTC dip can halt the bot with no trade taken, and a
    /// rally can trip the profit lock. Say so loudly at boot.
    /// </summary>
    /// <summary>
    /// Quote currency of a market symbol, for either venue's naming.
    ///
    /// WhiteBIT separates with an underscore (SOL_USDT); Bybit does not
    /// (SOLUSDT). Splitting on '_' therefore returned the whole symbol on
    /// Bybit, so USDT - the actual quote currency - was reported as a foreign
    /// asset and the bot advised converting away the one thing it should hold.
    /// </summary>
    internal static string QuoteCurrencyOf(string market)
    {
        if (market.Contains('_')) return market.Split('_')[^1];

        // Longest first, so USDT is not mistaken for USD.
        foreach (var q in new[] { "USDT", "USDC", "USD", "BTC", "ETH" })
            if (market.Length > q.Length && market.EndsWith(q, StringComparison.OrdinalIgnoreCase))
                return q;

        return market;
    }

    private async Task CheckCollateralDenominationAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, decimal> balances;
        try { balances = await _client.GetCollateralBalancesAsync(ct); }
        catch (ApiException ex) { Log.Warn($"could not read collateral balances: {ex.Message}"); return; }

        var quote = _cfg.Trading.Markets.Select(QuoteCurrencyOf).Distinct().ToList();
        var foreign = balances
            .Where(kv => kv.Value > 0 && !quote.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} {kv.Value:0.########}")
            .ToList();

        if (foreign.Count == 0)
        {
            Log.Info($"collateral is {string.Join("/", quote)} only — equity locks measure trading results");
            return;
        }

        var msg = $"collateral holds non-{string.Join("/", quote)} assets: {string.Join(", ", foreign)}. " +
                  "Their price moves count as PnL in the drawdown/profit locks and in the Telegram balance. " +
                  "Convert them to the quote currency or move them to the main account.";
        Log.Warn(msg);
        _telegram?.Send($"⚠️ {msg}");
    }

    /// <summary>
    /// Equity that moves with nothing open and nothing pending is not the
    /// bot's doing. Warn (at most hourly) so the cause gets looked at.
    /// </summary>
    private void NoteFlatEquity(decimal equity, IReadOnlyList<Position> positions)
    {
        var flat = positions.All(p => p.AbsAmount <= 0) && _pending.Count == 0;
        if (!flat || equity <= 0)
        {
            _flatEquityRef = null;
            return;
        }

        if (_flatEquityRef is not { } reference)
        {
            _flatEquityRef = equity;
            return;
        }

        var movePct = Math.Abs(equity - reference) / reference * 100m;
        if (movePct < 0.25m) return;
        if (DateTime.UtcNow - _lastFlatEquityWarnUtc < TimeSpan.FromHours(1)) { _flatEquityRef = equity; return; }

        _lastFlatEquityWarnUtc = DateTime.UtcNow;
        _flatEquityRef = equity;
        var message = $"equity moved {movePct:F2}% ({reference:F2} → {equity:F2}) with no position open — " +
                      "collateral is not quote-only, or a position exists that the bot cannot see. " +
                      "The drawdown/profit locks are reacting to that, not to trades.";
        Log.Warn(message);
        _telegram?.Send($"⚠️ {_venueLabel}: {message}");
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

        var summary = await _client.GetAccountSummaryAsync(ct);
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
        NoteFlatEquity(equity, positions);

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

        await ReconcilePendingAsync(positions, ct);

        var openNotional = positions.Sum(p => p.AbsAmount * p.BasePrice);

        // 1. Manage what is already open. Exits always come before entries.
        foreach (var pos in positions)
        {
            if (pos.Market is null) continue;
            await ManagePositionAsync(pos, ct);
        }
        PersistManagedPositions(positions);

        // 2. Scan every market, then take the single best setup. Filling
        //    slots in list order put ETH (first) on a stale trend and SOL
        //    (real trigger) in the same tick.
        await ConsiderEntriesAsync(equity, openNotional, positions, ct);

        if (ShouldStatusLog() && ShouldIdleStatusLog(positions))
        {
            var scale = _risk.RiskScale();
            Log.Info($"equity {equity:F2} | open {positions.Count} | notional {openNotional:F2} | " +
                     $"pending {_pending.Count} | trades today {_risk.EntriesToday}/{_cfg.Risk.MaxTradesPerDay} | " +
                     $"loss streak {_risk.ConsecutiveLosses}" +
                     (_risk.ConsecutiveSmallLosses > 0 ? $" ({_risk.ConsecutiveSmallLosses} fee-sized, {_risk.LossStreakUsd:0.00} USDT)" : "") +
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
    private static string FormatClose(ClosedTrade t, ClosedPnl rec, decimal pct, decimal equity, TimeSpan held)
    {
        var win = rec.NetPnl >= 0;
        var side = t.IsLong ? "long" : "short";

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
        sb.Append($"{(_live ? "🟢" : "⚪")} {_venueLabel} · {(_live ? "LIVE" : "DRY RUN")} · {BuildInfo.Display}\n");
        sb.Append($"Balance  {_lastEquity:F2} USDT ({session:+0.00;-0.00} session)\n");
        sb.Append($"Open     {real.Count}/{_cfg.Risk.MaxConcurrentPositions} · notional {notional:F2}\n");
        sb.Append($"Pending  {_pending.Count} · last tick {age}\n");
        var (attempts, fills) = PostOnlyFillStats();
        if (attempts > 0)
            sb.Append($"Passive fills  {fills}/{attempts} ({fills * 100m / attempts:F0}%) · last {_cfg.Trading.PostOnlyFillWarningWindowMinutes}m\n");
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
        if (real.Count == 0) return $"[{_venueLabel}] No open positions.";

        var sb = new System.Text.StringBuilder();
        sb.Append($"[{_venueLabel}]\n");
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
        market is null ? "?"
            : market.Replace("_USDT", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("USDT", "", StringComparison.OrdinalIgnoreCase);

    // ── Entries ─────────────────────────────────────────────────────────

    private sealed record ScannedEntry(
        TradeSignal Signal, OrderBook Book, MarketRules Rules, decimal EntryPrice, SizingResult Sizing,
        StandAsideSnapshot? Aside, decimal ConvictionScale);

    private async Task ConsiderEntriesAsync(
        decimal equity, decimal openNotional, IReadOnlyList<Position> positions, CancellationToken ct)
    {
        if (EntriesBlockedByVenue())
            return;

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

        // The bar probe is one public kline call. If the venue throttles or
        // drops it, skip this tick's scan rather than let the exception abort
        // the tick (exits have already run above, but the status line and
        // heartbeat would be lost too). First failure in a row goes to
        // Telegram, repeats stay in the console.
        bool newClosedBar;
        try
        {
            newClosedBar = await NoteClosedEntryBarAsync(ct);
            _barProbeFailures = 0;
        }
        catch (ApiException ex)
        {
            _barProbeFailures++;
            var line = $"entry scan skipped this tick ({_barProbeFailures} in a row) - {ex.Message}";
            if (_barProbeFailures == 1) Log.Warn(line); else Log.Info(line);
            return;
        }

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

        var conviction = RiskManager.ConvictionScale(
            signal.Score, _cfg.Signals.MinScore, _cfg.Risk.ConvictionRiskFloor);
        var sizing = _risk.Size(isLong, entryPrice, signal.Atr, equity, openNotional, rules, conviction);
        if (!sizing.Allowed)
        {
            if (ShouldLogSizingReject(market))
                Log.Info($"{market}: {signal.Direction} setup rejected by sizing - {sizing.Reason}");
            return null;
        }

        return new ScannedEntry(signal, book, rules, entryPrice, sizing, aside, conviction);
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
            $"risk {riskQuote:F2} | conv x{scanned.ConvictionScale:0.##} | {signal.Explain()}");

        foreach (var c in signal.Components.Where(c => c.Weight > 0))
            Log.Info($"    {c.Name,-10} {c.Direction,-5} w={c.Weight:F2}  {c.Detail}");

        // Remembered per market and attached when the fill shows up as a
        // position, so the journal can grade the trade against its setup.
        var riskScale = _risk.RiskScale(convictionScale: scanned.ConvictionScale);
        _entryContext[market] = (
            EntryContext.From(signal, scanned.Aside, riskScale, DateTime.UtcNow, scanned.ConvictionScale),
            DateTime.UtcNow);

        if (!_live)
        {
            Log.Info($"{market}: DRY RUN - no order sent");
            return;
        }

        var payload = new EntryLimitRequest
        {
            Market = market,
            Side = isLong ? "buy" : "sell",
            Amount = rules.FormatAmount(sizing.Amount),
            Price = rules.FormatPriceRaw(scanned.EntryPrice),
            PostOnly = _cfg.Trading.PostOnlyEntries,
            ClientOrderId = $"ta-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            StopLoss = rules.FormatPriceRaw(sizing.HardStopPrice),
            TakeProfit = rules.FormatPriceRaw(sizing.TakeProfitPrice)
        };

        try
        {
            var order = await _client.PlaceEntryLimitAsync(payload, ct);
            if (order is null) return;

            if (string.IsNullOrWhiteSpace(order.OrderId))
                Log.Warn($"{market}: venue returned no order id for {payload.ClientOrderId} - " +
                         "tracking it by client order id");

            _pending.Add(new PendingEntry(
                order.OrderId ?? "", order.ClientOrderId ?? payload.ClientOrderId, market, isLong, DateTime.UtcNow));
            Log.Trade($"{market}: entry order #{order.OrderId} placed ({order.Status}), left {order.Left}");
            NoteEntriesAccepted();
        }
        catch (ApiException ex)
        {
            if (IsEntryBlocked(ex))
            {
                BlockEntriesForVenue(market, ex);
                return;
            }
            Log.Warn($"{market}: entry rejected - {ex.Message}");
        }
    }

    private async Task ReconcilePendingAsync(IReadOnlyList<Position> positions, CancellationToken ct)
    {
        if (_pending.Count == 0) return;

        var timeout = TimeSpan.FromSeconds(_cfg.Trading.EntryOrderTimeoutSeconds);
        foreach (var p in _pending.ToList())
        {
            IReadOnlyList<OrderResponse> open;
            try { open = await _client.GetOpenOrdersAsync(p.Market, ct); }
            catch (ApiException ex) { Log.Warn($"{p.Market}: could not list open orders - {ex.Message}"); continue; }

            var still = open.FirstOrDefault(p.Is);
            if (still is null)
            {
                await SettleMissingEntryAsync(p, positions, ct);
                continue;
            }

            p.MissingSinceUtc = null;
            if (string.IsNullOrEmpty(p.OrderId) && !string.IsNullOrEmpty(still.OrderId))
                p.OrderId = still.OrderId;

            if (DateTime.UtcNow - p.PlacedUtc <= timeout) continue;
            if (p.NextCancelUtc is { } next && DateTime.UtcNow < next) continue;

            try
            {
                await _client.CancelOrderAsync(p.Market, p.OrderId, ct);
                Log.Trade($"{p.Market}: entry #{p.Label} not filled within {timeout.TotalSeconds:F0}s - " +
                          "cancelled (no cooldown consumed)");
                _pending.Remove(p);
            }
            catch (ApiException ex) when (IsOrderAlreadyGone(ex))
            {
                // It left the book between the listing and the cancel: filled or
                // cancelled elsewhere. Ask the venue rather than guess.
                Log.Info($"{p.Market}: entry #{p.Label} is already gone from the book - confirming outcome");
                await SettleMissingEntryAsync(p, positions, ct);
            }
            catch (ApiException ex)
            {
                p.CancelFailures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(p.CancelFailures - 1, 5))));
                p.NextCancelUtc = DateTime.UtcNow + backoff;
                var line = $"{p.Market}: cancel of #{p.Label} failed (attempt {p.CancelFailures}, " +
                           $"retry in {backoff.TotalSeconds:F0}s) - {ex.Message}";

                if (p.CancelFailures >= MaxCancelAttempts)
                {
                    // Stop pretending to manage it: an order that cannot be cancelled
                    // 10 times will not be cancelled by an 11th, and a tracked-but-stuck
                    // entry blocks every slot. Say so loudly instead.
                    Log.Error($"{line} - giving up; check the order in the exchange UI");
                    _telegram?.Send($"⚠️ {_venueLabel}: could not cancel the {Short(p.Market)} entry order " +
                                    $"after {p.CancelFailures} attempts. Cancel it manually.");
                    _pending.Remove(p);
                }
                else if (p.CancelFailures == 1 || p.CancelFailures % 5 == 0) Log.Warn(line);
                else Log.Info(line);
            }
            catch (ArgumentException ex)
            {
                Log.Error($"{p.Market}: cannot cancel entry - {ex.Message}");
                _pending.Remove(p);
            }
        }
    }

    private const int MaxCancelAttempts = 10;
    private static readonly TimeSpan EntryOutcomeGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An entry order is no longer on the open book. That alone does not mean
    /// it filled: it may have been rejected (post-only crossing the spread),
    /// cancelled, or expired. Only a fill starts the cooldown and rate limits.
    /// </summary>
    private async Task SettleMissingEntryAsync(PendingEntry p, IReadOnlyList<Position> positions, CancellationToken ct)
    {
        OrderResponse? info = null;
        if (!string.IsNullOrEmpty(p.OrderId))
        {
            try { info = await _client.GetOrderAsync(p.Market, p.OrderId, ct); }
            catch (ApiException ex) { Log.Info($"{p.Market}: could not read final state of #{p.Label} - {ex.Message}"); }
        }

        var holding = positions.Any(x =>
            x.AbsAmount > 0 &&
            string.Equals(x.Market, p.Market, StringComparison.OrdinalIgnoreCase) &&
            x.IsLong == p.IsLong);

        switch (ClassifyEntry(info, holding))
        {
            case EntryOutcome.Filled:
                _risk.RecordEntry(p.Market);
                NotePostOnlyOutcome(filled: true);
                Log.Trade($"{p.Market}: entry #{p.Label} filled" +
                          (info is { DealStock: > 0 } ? $" ({info.DealStock} of {info.Amount})" : ""));
                if (_cfg.Telegram.SendTrades)
                    _telegram?.Send($"Opened {Short(p.Market)} {(p.IsLong ? "long" : "short")}");
                _pending.Remove(p);
                break;

            case EntryOutcome.NotFilled:
                NotePostOnlyOutcome(filled: false);
                Log.Trade($"{p.Market}: entry #{p.Label} ended without a fill ({info?.Status}) - " +
                          "no entry recorded, no cooldown consumed");
                _pending.Remove(p);
                break;

            default:
                p.MissingSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - p.MissingSinceUtc.Value < EntryOutcomeGrace) break;

                Log.Warn($"{p.Market}: entry #{p.Label} left the book and its outcome could not be confirmed " +
                         $"in {EntryOutcomeGrace.TotalSeconds:F0}s - dropping it from tracking; any position " +
                         "that exists is still managed");
                _pending.Remove(p);
                break;
        }
    }

    public enum EntryOutcome { Filled, NotFilled, Unknown }

    /// <summary>
    /// Decides what happened to an entry order that is off the open book, from
    /// the venue's own final state when it has one, else from whether a
    /// position now exists on that market and side.
    /// </summary>
    public static EntryOutcome ClassifyEntry(OrderResponse? info, bool positionOnMarket)
    {
        if (info is not null)
        {
            if (info.DealStock > 0) return EntryOutcome.Filled;

            var status = (info.Status ?? "").Replace("_", "", StringComparison.Ordinal);
            if (status.Equals("Filled", StringComparison.OrdinalIgnoreCase)) return EntryOutcome.Filled;
            if (status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Deactivated", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Expired", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("PartiallyFilledCanceled", StringComparison.OrdinalIgnoreCase))
                return EntryOutcome.NotFilled;
        }

        return positionOnMarket ? EntryOutcome.Filled : EntryOutcome.Unknown;
    }

    /// <summary>
    /// Bybit answers a cancel for an order that already left the book with
    /// retCode 110001 ("order not exists or too late to cancel"). Retrying that
    /// is pointless - the order is gone, and the question is only how it ended.
    /// </summary>
    public static bool IsOrderAlreadyGone(ApiException ex)
    {
        var body = ex.Body ?? "";
        return body.Contains("110001", StringComparison.Ordinal)
            || body.Contains("order not exists", StringComparison.OrdinalIgnoreCase)
            || body.Contains("too late to cancel", StringComparison.OrdinalIgnoreCase);
    }

    // ── Venue entry restriction ─────────────────────────────────────────

    /// <summary>
    /// Whether the venue is refusing new orders for account reasons rather than
    /// for anything about this particular order. WhiteBIT answers every entry
    /// with "Margin trading is not available for your account due to trading
    /// restrictions"; retrying each tick only repeats the same rejection.
    /// </summary>
    public static bool IsEntryBlocked(ApiException ex)
    {
        if (ex.Status == HttpStatusCode.TooManyRequests || (int)ex.Status >= 500) return false;

        var body = ex.Body ?? "";
        return body.Contains("trading restrictions", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Margin trading is not available", StringComparison.OrdinalIgnoreCase)
            || body.Contains("account is restricted", StringComparison.OrdinalIgnoreCase)
            || body.Contains("retCode=10024", StringComparison.Ordinal)   // Bybit: compliance rules triggered
            || body.Contains("retCode=10027", StringComparison.Ordinal);  // Bybit: transactions banned
    }

    private bool EntriesBlockedByVenue()
    {
        if (DateTime.UtcNow >= _entriesBlockedUntilUtc) return false;

        if (DateTime.UtcNow - _lastEntryBlockedStatusLogUtc >= TimeSpan.FromMinutes(15))
        {
            _lastEntryBlockedStatusLogUtc = DateTime.UtcNow;
            Log.Info($"entries blocked on {_venueLabel} (account restriction) - next attempt after " +
                     $"{_entriesBlockedUntilUtc:HH:mm} UTC");
        }
        return true;
    }

    private void BlockEntriesForVenue(string market, ApiException ex)
    {
        _entriesBlockedUntilUtc = DateTime.UtcNow + EntryBlockReprobe;
        _lastEntryBlockedStatusLogUtc = DateTime.UtcNow;

        if (_entryBlockNotified)
        {
            Log.Warn($"{market}: {_venueLabel} still refuses entries - pausing for {EntryBlockReprobe.TotalHours:F0}h");
            return;
        }

        _entryBlockNotified = true;
        Log.Error($"{market}: {_venueLabel} refuses new orders on this account - {ex.Message}. " +
                  $"No entries will be attempted here for {EntryBlockReprobe.TotalHours:F0}h.");
        _telegram?.Send(
            $"🚫 {_venueLabel} is rejecting entry orders\n{ex.Message}\n\n" +
            $"No entries will be attempted on this venue for {EntryBlockReprobe.TotalHours:F0}h, then it tries once more. " +
            "Open positions here (if any) stay managed. Fix the account restriction in the exchange UI.");
    }

    private void NoteEntriesAccepted()
    {
        if (!_entryBlockNotified) return;
        _entryBlockNotified = false;
        _entriesBlockedUntilUtc = DateTime.MinValue;
        Log.Info($"{_venueLabel} accepted an entry again - restriction lifted");
        _telegram?.Send($"✅ {_venueLabel} accepted an entry order again. The earlier restriction has lifted.");
    }

    private void NotePostOnlyOutcome(bool filled)
    {
        if (!_cfg.Trading.PostOnlyEntries) return;

        var now = DateTime.UtcNow;
        lock (_postOnlyOutcomeLock)
            _postOnlyOutcomes.Enqueue(new EntryOutcomeSample(now, filled));
        var (attempts, fills) = PostOnlyFillStats(now);
        if (!ShouldWarnPostOnlyFillRate(attempts, fills, _cfg.Trading.PostOnlyFillWarningMinAttempts,
                _cfg.Trading.PostOnlyFillWarningMinRatePct))
            return;
        if (now - _lastPostOnlyFillWarningUtc < TimeSpan.FromHours(1)) return;

        _lastPostOnlyFillWarningUtc = now;
        var rate = fills * 100m / attempts;
        var message = $"post-only fill rate {fills}/{attempts} ({rate:F0}%) in the last " +
                      $"{_cfg.Trading.PostOnlyFillWarningWindowMinutes}m; below the " +
                      $"{_cfg.Trading.PostOnlyFillWarningMinRatePct:F0}% threshold. Consider a less passive entry policy.";
        Log.Warn(message);
        _telegram?.Send($"⚠️ {_venueLabel}: {message}");
    }

    private (int Attempts, int Fills) PostOnlyFillStats(DateTime? nowUtc = null)
    {
        lock (_postOnlyOutcomeLock)
        {
            var cutoff = (nowUtc ?? DateTime.UtcNow).AddMinutes(-_cfg.Trading.PostOnlyFillWarningWindowMinutes);
            while (_postOnlyOutcomes.Count > 0 && _postOnlyOutcomes.Peek().SettledUtc < cutoff)
                _postOnlyOutcomes.Dequeue();
            return (_postOnlyOutcomes.Count, _postOnlyOutcomes.Count(x => x.Filled));
        }
    }

    public static bool ShouldWarnPostOnlyFillRate(int attempts, int fills, int minAttempts, decimal minRatePct) =>
        minAttempts > 0 && attempts >= minAttempts && fills * 100m / attempts < minRatePct;

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
        try { closed = await _client.GetClosedPnlAsync(50, ct); }
        catch (ApiException ex) { Log.Warn($"could not read closed PnL - {ex.Message}"); return; }

        foreach (var t in _awaitingPnl.ToList())
        {
            var matched = MatchClosedRecords(
                closed, t.PositionId, t.Market, t.State.OpenedUtc, t.ClosedUtc, _bookedClosedPnl);

            // A partial take books its own record while the position is still
            // open. If only that one has appeared, the final close is still on its
            // way - booking now would count half the trade.
            if (matched.Count > 0 && t.State.PartialTaken &&
                matched.Sum(r => r.ClosedSize) < t.State.InitialSize * 0.98m &&
                DateTime.UtcNow - t.ClosedUtc < TimeSpan.FromMinutes(2))
                continue;

            if (matched.Count == 0)
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

            foreach (var r in matched) _bookedClosedPnl.Add(r.RecordKey);
            var rec = ClosedPnl.Merge(matched);

            var notional = rec.AvgEntryPrice * rec.ClosedSize;
            var pct = notional > 0 ? rec.NetPnl / notional * 100m : 0m;

            _risk.RecordTradeResult(profitable: rec.NetPnl > 0, netPnl: rec.NetPnl);
            Log.Trade($"{t.Market} #{t.PositionId} closed: net {rec.NetPnl:+0.0000;-0.0000} USDT " +
                      $"({pct:+0.00;-0.00}%), fees {rec.TotalTradeFee:F4}, funding {rec.TotalFunding:F4}" +
                      (matched.Count > 1 ? $" [{matched.Count} records]" : ""));

            var record = BuildRecord(t, rec, pct);
            _journal.Append(record);

            if (_cfg.Telegram.SendTrades)
                _telegram?.Send(FormatClose(t, rec, pct, equity, TimeSpan.FromMinutes(record.HeldMinutes)));

            _awaitingPnl.Remove(t);
        }

        // Bounded: the venue only returns recent records, so older keys can never match again.
        if (_bookedClosedPnl.Count > 500) _bookedClosedPnl.Clear();
    }

    /// <summary>
    /// Picks the closed-PnL records that belong to one trade. Bybit's position
    /// id is synthetic (symbol + side), so the same id recurs for every trade
    /// on that symbol: matching on it alone returned the previous trade's
    /// record whenever the new one had not posted yet. Records are therefore
    /// also limited to the trade's own time window and to those not already
    /// booked to an earlier trade.
    /// </summary>
    public static IReadOnlyList<ClosedPnl> MatchClosedRecords(
        IReadOnlyList<ClosedPnl> closed, long positionId, string market,
        DateTime openedUtc, DateTime closedUtc, ISet<string> booked)
    {
        var from = openedUtc - TimeSpan.FromSeconds(60);
        var to = closedUtc + TimeSpan.FromMinutes(10);

        var candidates = closed
            .Where(c => c.CloseDate > 0 &&
                        !booked.Contains(c.RecordKey) &&
                        string.Equals(c.Market, market, StringComparison.OrdinalIgnoreCase))
            .Where(c =>
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(c.CloseDate).UtcDateTime;
                return at >= from && at <= to;
            })
            .ToList();

        var byId = candidates.Where(c => c.PositionId == positionId).ToList();
        // Venues that report real position ids (WhiteBIT) can fall back to market + time.
        // Venues that report an order id (Bybit) cannot: a miss there means "not posted yet".
        var chosen = byId.Count > 0 ? byId : candidates.Where(c => c.OrderId.Length == 0).ToList();
        return chosen.OrderBy(c => c.CloseDate).ToList();
    }

    private TradeRecord BuildRecord(ClosedTrade t, ClosedPnl rec, decimal pct)
    {
        var m = t.State;
        var stopDistance = m.EntryAtr * _cfg.Trading.StopLossAtrMult;
        var initialRisk = m.InitialSize * stopDistance;
        // The exchange's closed-PnL row carries an opening time only on venues that
        // track real positions; otherwise the bot's own verified opening time is used.
        var opened = rec.OpenDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(rec.OpenDate).UtcDateTime : m.OpenedUtc;
        var closed = rec.CloseDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(rec.CloseDate).UtcDateTime : t.ClosedUtc;
        if (closed < opened) closed = opened;

        return new TradeRecord
        {
            PositionId = t.PositionId,
            Market = t.Market,
            Side = t.IsLong ? "long" : "short",
            OpenedUtc = opened,
            ClosedUtc = closed,
            OpenedSource = m.OpenedSource,
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

        // A rejected close used to retry on the very next tick. Against a
        // permanent rejection (the exchange disabling margin trading on the
        // account) that became one failed API call and one Telegram error every
        // two seconds, indefinitely. The gate sits here, after the stop/high-water
        // updates, so a stuck position keeps being tracked and keeps appearing in
        // the status log while it waits.
        if (m.CloseRetryAfterUtc is { } retryAfter && DateTime.UtcNow < retryAfter)
        {
            if (ShouldStatusLog())
                Log.Info($"{pos.Market} #{pos.PositionId}: exit pending ({exitReason}) - " +
                         $"close retry in {(retryAfter - DateTime.UtcNow).TotalSeconds:F0}s after {m.CloseFailures} failure(s) | " +
                         $"mark {mark} | uPnL {floating:F4} ({rMultiple:F2}R)");
            return;
        }

        // First attempt is a trade event; retries are console-only so a blocked
        // exit does not keep pinging Telegram.
        var closingLine = $"{pos.Market} #{pos.PositionId}: closing - {exitReason} | uPnL {floating:F4}";
        if (m.CloseFailures == 0) Log.Trade(closingLine);
        else Log.Info($"{closingLine} (retry {m.CloseFailures + 1})");

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
            var failures = m.CloseFailures;
            m.CloseFailures = 0;
            m.CloseRetryAfterUtc = null;
            Log.Trade($"{pos.Market} #{pos.PositionId}: close requested" +
                      (failures > 0 ? $" (accepted after {failures} rejected attempt(s))" : ""));

            if (m.CloseBlockedNotified)
            {
                // The restriction that blocked us has lifted. Entries stay
                // paused on purpose: whatever the exchange did to the account
                // is for the operator to look at before the bot trades again.
                m.CloseBlockedNotified = false;
                _telegram?.Send(
                    $"✅ {_venueLabel} accepted the close of {Short(pos.Market)} " +
                    $"{(m.IsLong ? "long" : "short")} after {failures} rejected attempt(s).\n" +
                    "Entries are still paused on this venue - send /resume once you have checked the account.");
            }
        }
        catch (ApiException ex)
        {
            m.CloseFailures++;
            m.CloseRequestedUtc = null;

            if (IsExitBlocked(ex))
            {
                // The exchange is refusing to let us out. Retrying every tick
                // cannot help, and the only safe response is to stop opening
                // anything new: a bot that cannot exit must not enter. Pause
                // rather than halt, because halting triggers a flatten that
                // would fail the same way.
                m.CloseRetryAfterUtc = DateTime.UtcNow.AddMinutes(5);

                if (!m.CloseBlockedNotified)
                {
                    m.CloseBlockedNotified = true;
                    m.CloseBlockedRemindedUtc = DateTime.UtcNow;
                    Log.Error($"{pos.Market} #{pos.PositionId}: EXIT BLOCKED by the exchange - {ex.Message}");
                    _risk.Pause("- the venue is rejecting closes, so new positions could not be managed");

                    _telegram?.Send(
                        $"🚫 {_venueLabel} is refusing to close positions\n" +
                        $"{Short(pos.Market)} {(m.IsLong ? "long" : "short")} is stuck open.\n" +
                        $"{ex.Message}\n\n" +
                        "Entries are paused here. Close it manually in the exchange UI and " +
                        "check the account restriction. Retrying every 5 min in case it lifts; " +
                        "send /resume once the account is trading again.");
                }
                else
                {
                    // Console only. A Telegram line per retry is the flood this
                    // code exists to prevent; one reminder an hour is enough.
                    Log.Info($"{pos.Market} #{pos.PositionId}: exit still blocked (attempt {m.CloseFailures}) - next try in 5 min");
                    if (DateTime.UtcNow - (m.CloseBlockedRemindedUtc ?? DateTime.MinValue) >= TimeSpan.FromHours(1))
                    {
                        m.CloseBlockedRemindedUtc = DateTime.UtcNow;
                        _telegram?.Send(
                            $"🚫 {_venueLabel}: {Short(pos.Market)} {(m.IsLong ? "long" : "short")} still cannot be closed " +
                            $"({m.CloseFailures} attempts). Close it in the exchange UI.");
                    }
                }
                return;
            }

            // Transient: back off instead of hammering, 5s doubling to 5 min.
            // The first failure and every fifth one reach Telegram; the rest
            // stay in the console so a flaky endpoint is visible but not noisy.
            var backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(m.CloseFailures - 1, 8))));
            m.CloseRetryAfterUtc = DateTime.UtcNow + backoff;
            var line = $"{pos.Market} #{pos.PositionId}: close failed (attempt {m.CloseFailures}, " +
                       $"retrying in {backoff.TotalSeconds:F0}s) - {ex.Message}";
            if (m.CloseFailures == 1 || m.CloseFailures % 5 == 0) Log.Error(line);
            else Log.Info(line);
        }
    }

    /// <summary>
    /// Whether the exchange is refusing the close outright rather than failing
    /// transiently. These do not get better by retrying: the account has lost
    /// the permission the exit needs.
    /// </summary>
    public static bool IsExitBlocked(ApiException ex)
    {
        // 429 / 5xx are retried inside the client already; if they still
        // surface here the venue is struggling, not refusing.
        if (ex.Status == HttpStatusCode.TooManyRequests || (int)ex.Status >= 500) return false;

        var body = ex.Body ?? "";
        return body.Contains("trading restrictions", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Margin trading is not available", StringComparison.OrdinalIgnoreCase)
            || body.Contains("account is restricted", StringComparison.OrdinalIgnoreCase)
            || body.Contains("\"code\":10008", StringComparison.Ordinal);
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
            var order = await _client.PlaceReduceOnlyMarketAsync(new ReduceOnlyMarketRequest
            {
                Market = pos.Market!,
                Side = m.IsLong ? "sell" : "buy",
                Amount = rules.FormatAmount(closeAmount),
                PositionSide = pos.PositionSide,
                ClientOrderId = $"tp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
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
        var market = pos.Market!;
        var isLong = pos.IsLong;
        var sign = isLong ? 1m : -1m;
        var entry = pos.BasePrice;

        if (entry <= 0)
        {
            Log.Warn($"{market}: cannot adopt #{pos.PositionId}, no usable base price");
            return null;
        }

        // A fill of our own entry order shows up here too. If this session
        // placed an order on this market a few minutes ago, the position is
        // ours and carries that signal's context; otherwise it is adopted.
        EntryContext? context = null;
        DateTime? placedUtc = null;
        var hasFreshContext = _entryContext.TryGetValue(market, out var ctx) &&
                              DateTime.UtcNow - ctx.PlacedUtc < TimeSpan.FromMinutes(15);
        if (hasFreshContext)
        {
            context = ctx.Context;
            placedUtc = ctx.PlacedUtc;
        }

        // State written by an earlier run of this very trade (restart mid-trade).
        // Ignored when this session has just placed a newer order on the market:
        // that position is a new trade even if the symbol and side repeat.
        var key = SavedPosition.KeyOf(market, isLong);
        SavedPosition? saved = null;
        if (_saved.TryGetValue(key, out var candidate) &&
            candidate.Matches(entry, pos.AbsAmount) &&
            (placedUtc is not { } placed || candidate.OpenedUtc >= placed - TimeSpan.FromSeconds(10)))
            saved = candidate;

        var (opened, source) = await ResolveOpenedAsync(pos, saved, placedUtc, ct);

        ManagedPosition m;
        if (saved is not null)
        {
            m = new ManagedPosition
            {
                PositionId = pos.PositionId,
                Market = market,
                IsLong = isLong,
                EntryPrice = saved.EntryPrice,
                EntryAtr = saved.EntryAtr,
                OpenedUtc = opened,
                OpenedSource = source,
                Adopted = saved.Adopted,
                InitialSize = saved.InitialSize,
                Entry = saved.Entry,
                HighWater = saved.HighWater,
                StopPrice = saved.StopPrice,
                TakeProfitPrice = saved.TakeProfitPrice,
                MovedToBreakEven = saved.MovedToBreakEven,
                MaxFavourableR = saved.MaxFavourableR,
                PartialTaken = saved.PartialTaken
            };
        }
        else
        {
            var candles = await _client.GetKlinesAsync(
                market, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
            if (candles.Count < _cfg.Signals.AtrPeriod + 2)
            {
                Log.Warn($"{market}: cannot adopt #{pos.PositionId}, not enough candles for ATR");
                return null;
            }

            var atrSeries = Ta.Atr(candles, _cfg.Signals.AtrPeriod);
            var atr = Ta.IsUsable(atrSeries[^1]) ? (decimal)atrSeries[^1] : 0m;
            if (atr <= 0)
            {
                Log.Warn($"{market}: cannot adopt #{pos.PositionId}, ATR unavailable");
                return null;
            }

            m = new ManagedPosition
            {
                PositionId = pos.PositionId,
                Market = market,
                IsLong = isLong,
                EntryPrice = entry,
                EntryAtr = atr,
                OpenedUtc = opened,
                OpenedSource = source,
                Adopted = context is null,
                InitialSize = pos.AbsAmount,
                Entry = context,
                HighWater = entry,
                StopPrice = rules.SnapPriceForSide(entry - sign * atr * _cfg.Trading.StopLossAtrMult, isBuy: !isLong),
                TakeProfitPrice = rules.SnapPriceForSide(entry + sign * atr * _cfg.Trading.TakeProfitAtrMult, isBuy: isLong)
            };
        }

        if (hasFreshContext) _entryContext.Remove(market);
        _saved.Remove(key);

        var heldMin = (DateTime.UtcNow - opened).TotalMinutes;
        Log.Trade($"{market}: {(saved is not null ? "resumed" : m.Adopted ? "adopted" : "managing")} #{pos.PositionId} " +
                  $"{(isLong ? "LONG" : "SHORT")} entry {m.EntryPrice} stop {m.StopPrice} tp {m.TakeProfitPrice} " +
                  $"(ATR {m.EntryAtr}) | opened {opened:HH:mm:ss} UTC via {source}, age {heldMin:F1}m");
        return m;
    }

    /// <summary>
    /// Establishes when the position now held was opened, preferring evidence
    /// about this specific trade. Bybit's createdTime is deliberately not one
    /// of the sources: it is the symbol's first-ever position time.
    ///   venue       the position's own openTime
    ///   executions  replay of fills back to the last flat point
    ///   saved       the time recorded before a restart
    ///   placed      when this session placed the entry order
    ///   first-seen  now - never older than the bot actually knows
    /// Each candidate must pass <see cref="OpenTimeRules.Verify"/>.
    /// </summary>
    private async Task<(DateTime Opened, string Source)> ResolveOpenedAsync(
        Position pos, SavedPosition? saved, DateTime? placedUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (OpenTimeRules.Verify(pos.OpenedAtUtc, now, placedUtc) is { } fromVenue)
            return (fromVenue, "venue");

        try
        {
            var fromHistory = await _client.ResolvePositionOpenedAsync(pos, ct);
            if (OpenTimeRules.Verify(fromHistory, now, placedUtc) is { } v)
                return (v, "executions");
            if (fromHistory is not null)
                Log.Warn($"{pos.Market}: execution history put the opening at {fromHistory:u}, which is " +
                         "inconsistent with this session's entry order - ignored");
        }
        catch (ApiException ex)
        {
            Log.Warn($"{pos.Market}: could not read execution history for the opening time - {ex.Message}");
        }

        if (saved is not null && OpenTimeRules.Verify(saved.OpenedUtc, now, placedUtc) is { } fromSaved)
            return (fromSaved, "saved");

        if (placedUtc is { } placed)
            return (placed, "placed");

        return (now, "first-seen");
    }

    // ── Position state persistence ──────────────────────────────────────

    private void LoadSavedPositions()
    {
        foreach (var sp in _positionStore.Load())
        {
            if (string.IsNullOrWhiteSpace(sp.Market)) continue;
            _saved[sp.Key] = sp;
        }

        if (_saved.Count > 0)
            Log.Info($"loaded saved state for {_saved.Count} position(s) from {_positionStore.FilePath}: " +
                     string.Join(", ", _saved.Values.Select(v => $"{v.Market} {(v.IsLong ? "L" : "S")} opened {v.OpenedUtc:u}")));
    }

    /// <summary>
    /// Writes the state of every live position. A saved record whose position
    /// the exchange still reports is kept even before it is adopted, so a failed
    /// adoption (missing candles, say) cannot wipe the only copy of its opening time.
    /// </summary>
    private void PersistManagedPositions(IReadOnlyList<Position> positions)
    {
        if (!_live || !_positionStore.Enabled) return;

        var list = _managed.Values.Select(ToSaved).ToList();
        foreach (var kv in _saved)
        {
            var stillOpen = positions.Any(p =>
                p.AbsAmount > 0 && p.Market is not null &&
                SavedPosition.KeyOf(p.Market, p.IsLong) == SavedPosition.KeyOf(kv.Value.Market, kv.Value.IsLong));
            if (stillOpen && list.All(x => x.Key != kv.Key)) list.Add(kv.Value);
        }
        list = list.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

        var keys = string.Join(",", list.Select(x => x.Key));
        var setChanged = keys != _lastPositionKeys;
        if (!setChanged && DateTime.UtcNow - _lastPositionSaveUtc < TimeSpan.FromSeconds(10)) return;

        _positionStore.Save(list);
        _lastPositionKeys = keys;
        _lastPositionSaveUtc = DateTime.UtcNow;
    }

    private static SavedPosition ToSaved(ManagedPosition m) => new()
    {
        Market = m.Market,
        IsLong = m.IsLong,
        OpenedUtc = m.OpenedUtc,
        OpenedSource = m.OpenedSource,
        EntryPrice = m.EntryPrice,
        EntryAtr = m.EntryAtr,
        InitialSize = m.InitialSize,
        StopPrice = m.StopPrice,
        TakeProfitPrice = m.TakeProfitPrice,
        HighWater = m.HighWater,
        MovedToBreakEven = m.MovedToBreakEven,
        MaxFavourableR = m.MaxFavourableR,
        PartialTaken = m.PartialTaken,
        Adopted = m.Adopted,
        Entry = m.Entry
    };

    private bool ShouldStatusLog()
    {
        var every = Math.Max(1, _cfg.Trading.StatusLogEveryTicks);
        return _tick % every == 1;
    }

    /// <summary>
    /// Eighteen identical equity lines per 15m bar with nothing open is noise.
    /// While flat, throttle the status line to IdleStatusLogMinutes.
    /// </summary>
    private bool ShouldIdleStatusLog(IReadOnlyList<Position> positions)
    {
        var idle = positions.Count == 0 && _pending.Count == 0;
        if (!idle || _cfg.Trading.IdleStatusLogMinutes <= 0) return true;
        if (DateTime.UtcNow - _lastIdleStatusUtc < TimeSpan.FromMinutes(_cfg.Trading.IdleStatusLogMinutes)) return false;
        _lastIdleStatusUtc = DateTime.UtcNow;
        return true;
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
                Log.Info($"{p.Market}: cancelled resting entry #{p.Label} on shutdown");
            }
            catch (Exception ex)
            {
                Log.Warn($"{p.Market}: could not cancel #{p.Label} on shutdown - {ex.Message}");
            }
        }

        if (_managed.Count > 0)
            Log.Warn($"{_managed.Count} position(s) left open. They keep their exchange-side stop-loss, " +
                     "but no trailing or time stop until the bot is restarted.");
    }
}
