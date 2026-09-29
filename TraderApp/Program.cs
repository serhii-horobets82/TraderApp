using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using TraderApp.Backtest;
using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Notifications;
using TraderApp.Trading;

namespace TraderApp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // A mistyped flag must never fall through to live trading.
        var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Where(a => !KnownFlags.Contains(a))
            .ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"unknown option(s): {string.Join(" ", unknown)}");
            Console.Error.WriteLine("known: " + string.Join(" ", KnownFlags.OrderBy(f => f)));
            return 2;
        }
        if (args.Contains("--signal-quality", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--backtest", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--signal-quality is a --backtest option: use --backtest --signal-quality [--days N]");
            return 2;
        }

        var config = LoadConfig(args);

        var signalsOnly = args.Contains("--signals-only", StringComparer.OrdinalIgnoreCase);
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--backtest", StringComparer.OrdinalIgnoreCase))
        {
            var days = 30;
            var idx = Array.FindIndex(args, a => a.Equals("--days", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 1 < args.Length) int.TryParse(args[idx + 1], out days);
            var sweep = args.Contains("--sweep", StringComparer.OrdinalIgnoreCase);
            decimal fee = 0m;
            var fi = Array.FindIndex(args, a => a.Equals("--fee", StringComparison.OrdinalIgnoreCase));
            if (fi >= 0 && fi + 1 < args.Length) decimal.TryParse(args[fi + 1],
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out fee);
            var endAgo = 0;
            var ei = Array.FindIndex(args, a => a.Equals("--end-days-ago", StringComparison.OrdinalIgnoreCase));
            if (ei >= 0 && ei + 1 < args.Length) int.TryParse(args[ei + 1], out endAgo);
            var sq = args.Contains("--signal-quality", StringComparer.OrdinalIgnoreCase);
            return await BacktestRunner.RunAsync(config, Math.Clamp(days, 1, 365), sweep,
                CancellationToken.None, fee, Math.Clamp(endAgo, 0, 720), sq);
        }

        if (args.Contains("--telegram-test", StringComparer.OrdinalIgnoreCase))
            return await TelegramNotifier.RunSetupCheckAsync(config.Telegram, CancellationToken.None);

        Log.ToFile(Path.Combine(AppContext.BaseDirectory, "logs", $"trader-{DateTime.UtcNow:yyyyMMdd}.log"));

        var errors = config.Validate();
        if (signalsOnly)
        {
            // Public endpoints only - credentials are irrelevant here.
            errors = errors
                .Where(e => !e.StartsWith("Api.", StringComparison.Ordinal)
                            && !e.StartsWith("Bybit.Key", StringComparison.Ordinal)
                            && !e.StartsWith("Bybit.Secret", StringComparison.Ordinal))
                .ToList();
        }
        if (errors.Count > 0)
        {
            foreach (var e in errors) Log.Error(e);
            Log.Error("configuration is invalid - aborting");
            return 1;
        }

        // Any of the three can force dry-run, and none of them can undo another.
        // The config value has to be honoured or a deployed instance could not be
        // put into dry-run without a redeploy - set
        // TRADER_Trading__DryRun=true in Railway variables instead.
        config.Trading.DryRun = config.Trading.DryRun || dryRun || signalsOnly;
        config.Trading.IUnderstandThisTradesRealMoney = true;
        var live = !signalsOnly && !dryRun;

        ExchangeMode exchangeMode;
        try { exchangeMode = config.ResolveExchangeMode(); }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            return 1;
        }

        PrintBanner(config, live, signalsOnly, exchangeMode);

        using var cts = new CancellationTokenSource();
        var venues = VenueHost.Build(config, exchangeMode);
        await using var telegram = new TelegramNotifier(config.Telegram);
        if (telegram.IsConfigured)
        {
            Log.SetNotifier(telegram.SendFromLog);
            Log.Info($"Telegram notifications on (chat {config.Telegram.ChatId})");
        }
        else if (config.Telegram.Enabled)
        {
            Log.Warn("Telegram is enabled but has no ChatId - run with --telegram-test to find it");
        }

        var stopReason = "finished";

        // Exit code for a restart the operator asked for.
        //
        // Railway's ALWAYS restart policy is not available on Free/Trial plans;
        // those silently fall back to the default ON_FAILURE, which only
        // restarts on a NON-ZERO exit. Exiting 0 after /restart therefore left
        // the service "Completed" and stopped. Exiting non-zero restarts under
        // ON_FAILURE and ALWAYS alike, and systemd's Restart=always covers it
        // too - so this works on every host without depending on the plan.
        //
        // A genuine stop (SIGTERM from a deploy, Ctrl+C) still exits 0: that is
        // Railway asking the process to go away, not a request to come back.
        const int exitRestartRequested = 75;
        var restartRequested = false;

        void RequestShutdown(string source, bool restart = false)
        {
            stopReason = source;
            restartRequested |= restart;
            Log.Warn($"shutdown requested ({source}) - finishing the current cycle");
            if (!cts.IsCancellationRequested) cts.Cancel();
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestShutdown("Ctrl+C");
        };

        // ── Telegram commands ────────────────────────────────────────────
        // "Restart" is a graceful exit with a non-zero code, which the
        // supervisor turns into a restart on the current image. No Railway API
        // token is needed, which would otherwise have to live in the container
        // with rights over the whole project.
        //
        // The non-zero code is load-bearing: see exitRestartRequested above.
        // Run locally without a supervisor and /restart simply stops the bot.
        await using var commands = new TelegramCommandListener(config.Telegram, telegram.Send);

        commands.On("/restart", () =>
        {
            RequestShutdown("Telegram /restart", restart: true);
            return "♻️ Restarting. Open positions keep their exchange-side stop and are re-adopted on boot.";
        });

        commands.On("/version", () => $"{BuildInfo.Display}\nbuilt {BuildInfo.BuiltUtc} UTC");

        commands.On("/help", () =>
            "Commands:\n" +
            string.Join("\n", commands.Commands.OrderBy(c => c)));

        // Containers and orchestrators stop a process with SIGTERM, which
        // Console.CancelKeyPress does not see. Without this the bot is killed
        // mid-cycle: resting entry orders are never cancelled and queued
        // Telegram messages are lost.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            RequestShutdown("Railway/SIGTERM");
        });

        var traders = new List<AutoTrader>();
        try
        {
            // Polling runs alongside the trader. It is deliberately not awaited
            // together with it: a command listener that died must never stop the
            // trading loop, and the loop finishing must not wait on a 30s poll.
            var listening = commands.RunAsync(cts.Token);

            if (signalsOnly)
            {
                var monitors = venues.Select(v =>
                {
                    Log.Info($"[{v.Label}] signals-only on {string.Join(", ", v.Config.Trading.Markets)}");
                    return new SignalMonitor(v.Config, v.Client).RunAsync(cts.Token);
                });
                await Task.WhenAll(monitors);
            }
            else
            {
                var runs = new List<Task>();
                foreach (var (label, venueCfg, client) in venues)
                {
                    Log.Info($"[{label}] starting runner · markets {string.Join(", ", venueCfg.Trading.Markets)}");
                    var trader = new AutoTrader(venueCfg, client, telegram);
                    traders.Add(trader);
                    runs.Add(trader.RunAsync(cts.Token));
                }

                if (traders.Count == 1)
                {
                    var trader = traders[0];
                    commands.On("/status", () => trader.DescribeStatus());
                    commands.On("/positions", () => trader.DescribePositions());
                    commands.On("/pause", () => trader.Pause());
                    commands.On("/resume", () => trader.Resume());
                }
                else
                {
                    commands.On("/status", () => string.Join("\n───\n", traders.Select(t => t.DescribeStatus())));
                    commands.On("/positions", () => string.Join("\n───\n", traders.Select(t => t.DescribePositions())));
                    commands.On("/pause", () => string.Join("\n", traders.Select(t => t.Pause())));
                    commands.On("/resume", () => string.Join("\n", traders.Select(t => t.Resume())));
                }

                await Task.WhenAll(runs);
            }

            // The listener is cooperative and swallows its own errors; once the
            // trading loop is done the process is about to exit anyway.
            _ = listening;
        }
        catch (InvalidOperationException ex)
        {
            stopReason = ex.Message;
            Log.Error(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            stopReason = $"crash: {ex.Message}";
            Log.Error($"fatal: {ex}");
            return 2;
        }
        finally
        {
            foreach (var t in traders)
            {
                try { await t.DisposeAsync(); } catch { /* best effort */ }
            }
            foreach (var (_, _, client) in venues)
            {
                try { client.Dispose(); } catch { /* best effort */ }
            }

            // Railway sends SIGTERM to the outgoing replica on every deploy.
            // That is not an outage — the new replica already sent Started.
            // Alert Telegram and fail the watchdog only when this process
            // died unexpectedly, so a missed ping still covers hard kills.
            var planned = restartRequested
                          || stopReason is "Railway/SIGTERM" or "Ctrl+C" or "finished";
            if (!planned)
            {
                await telegram.SendNowAsync($"TraderApp is down ({stopReason}).");
                await telegram.SignalWatchdogDownAsync();
            }
            else if (stopReason == "Ctrl+C")
            {
                await telegram.SendNowAsync(
                    "TraderApp stopped (Ctrl+C). Open positions keep their exchange stop.");
            }

            Log.Info($"stopped ({stopReason})"
                     + (restartRequested ? $" - exiting {exitRestartRequested} so the supervisor restarts us" : ""));
            Log.SetNotifier(null);
            Log.Close();
        }

        return restartRequested ? exitRestartRequested : 0;
    }

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--signals-only", "--dry-run", "--backtest", "--days", "--sweep", "--fee",
        "--end-days-ago", "--signal-quality", "--telegram-test",
    };

    private static BotConfig LoadConfig(string[] _)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("TRADER_");

        var cfg = builder.Build().Get<BotConfig>() ?? new BotConfig();

        // Convenience aliases so the usual WhiteBIT / Bybit variable names work too.
        cfg.Api.Key = FirstNonEmpty(cfg.Api.Key, Environment.GetEnvironmentVariable("WHITEBIT_API_KEY"));
        cfg.Api.Secret = FirstNonEmpty(cfg.Api.Secret, Environment.GetEnvironmentVariable("WHITEBIT_API_SECRET"));
        cfg.Bybit.Key = FirstNonEmpty(cfg.Bybit.Key, Environment.GetEnvironmentVariable("BYBIT_API_KEY"));
        cfg.Bybit.Secret = FirstNonEmpty(cfg.Bybit.Secret, Environment.GetEnvironmentVariable("BYBIT_API_SECRET"));
        cfg.Telegram.BotToken = FirstNonEmpty(cfg.Telegram.BotToken, Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
        cfg.Telegram.ChatId = FirstNonEmpty(cfg.Telegram.ChatId, Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
        cfg.Telegram.WatchdogPingUrl = FirstNonEmpty(cfg.Telegram.WatchdogPingUrl,
            Environment.GetEnvironmentVariable("HEALTHCHECK_PING_URL"));

        // Credentials pasted through a shell or a prompt routinely pick up
        // trailing whitespace, which silently breaks HMAC signing.
        cfg.Api.Key = cfg.Api.Key.Trim();
        cfg.Api.Secret = cfg.Api.Secret.Trim();
        cfg.Bybit.Key = cfg.Bybit.Key.Trim();
        cfg.Bybit.Secret = cfg.Bybit.Secret.Trim();
        cfg.Telegram.BotToken = cfg.Telegram.BotToken.Trim();
        cfg.Telegram.ChatId = cfg.Telegram.ChatId.Trim();

        return cfg;
    }

    private static string FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : (b ?? "");

    private static void PrintBanner(BotConfig c, bool live, bool signalsOnly, ExchangeMode mode)
    {
        Console.WriteLine();
        Console.WriteLine($"  TraderApp {BuildInfo.Display}");
        Console.WriteLine("  ---------------------------------------");
        Console.ForegroundColor = live ? ConsoleColor.Red : ConsoleColor.Green;
        Console.WriteLine(signalsOnly
            ? "  MODE          : SIGNALS ONLY - public data, no API key used"
            : live
                ? "  MODE          : LIVE - real orders, real money"
                : "  MODE          : DRY RUN - decisions logged, nothing sent");
        Console.ResetColor();
        Console.WriteLine($"  Exchange      : {mode}");
        if (mode is ExchangeMode.WhiteBIT or ExchangeMode.Both)
        {
            Console.WriteLine($"  WhiteBIT      : {c.Api.BaseUrl}");
            Console.WriteLine($"  WB markets    : {string.Join(", ", c.Trading.Markets)}");
        }
        if (mode is ExchangeMode.Bybit or ExchangeMode.Both)
        {
            var bbMarkets = c.Bybit.Markets.Count > 0
                ? c.Bybit.Markets
                : c.Trading.Markets.Select(m => m.Replace("_", "", StringComparison.Ordinal)).ToList();
            Console.WriteLine($"  Bybit         : {c.Bybit.BaseUrl} ({c.Bybit.Category})");
            Console.WriteLine($"  BB markets    : {string.Join(", ", bbMarkets)}");
        }
        Console.WriteLine($"  Timeframes    : {c.Trading.EntryInterval} entry / {c.Trading.RegimeInterval} regime");
        Console.WriteLine($"  Leverage      : {c.Trading.Leverage}x");
        Console.WriteLine($"  Risk/trade    : {c.Risk.RiskPerTradePct}% of equity" +
                          (c.Risk.ConvictionRiskFloor < 1
                              ? $" (conviction floor x{c.Risk.ConvictionRiskFloor:0.##})"
                              : ""));
        Console.WriteLine($"  Stop / target : {c.Trading.StopLossAtrMult}x ATR / {c.Trading.TakeProfitAtrMult}x ATR");
        Console.WriteLine($"  Hard stop     : {c.Trading.HardStopMultiplier}x bot stop, attached to the entry order");
        Console.WriteLine($"  Max positions : {c.Risk.MaxConcurrentPositions} (max {c.Risk.MaxTotalNotionalUsd} notional)");
        Console.WriteLine($"  Daily cutout  : -{c.Risk.MaxDailyDrawdownPct}% session / -{c.Risk.MaxPeakDrawdownPct}% from peak");
        Console.WriteLine($"  Profit lock   : +{c.Risk.MaxDailyProfitPct}% session → {(c.Risk.ProfitLockRiskScale > 0 ? $"risk x{c.Risk.ProfitLockRiskScale:0.##}" : "HALT")} (0 = off)");
        Console.WriteLine($"  Weekend risk  : x{c.Risk.WeekendRiskScale:0.##} · entry hours {(c.Trading.EntryHoursUtcStart == c.Trading.EntryHoursUtcEnd ? "any" : $"{c.Trading.EntryHoursUtcStart:00}-{c.Trading.EntryHoursUtcEnd:00} UTC")}");
        Console.WriteLine($"  Partial take  : {(c.Trading.PartialTakeAtR > 0 ? $"{c.Trading.PartialTakeFraction:P0} at {c.Trading.PartialTakeAtR}R, rest to break-even" : "off")} · time stop {c.Trading.MaxPositionMinutes}m");
        Console.WriteLine($"  Book depth    : {(c.Signals.MinBookDepthUsd > 0 ? $"min {c.Signals.MinBookDepthUsd:F0} USDT thin side" : "unchecked")} · journal {(string.IsNullOrWhiteSpace(c.Trading.TradeJournalPath) ? "off" : c.Trading.TradeJournalPath)}");
        Console.WriteLine($"  Trade budget  : {c.Risk.MaxTradesPerHour}/hour, {c.Risk.MaxTradesPerDay}/day UTC");
        Console.WriteLine($"  Loss streak   : {c.Risk.MaxConsecutiveLosses} → {(c.Risk.HaltOnConsecutiveLosses ? "HALT" : $"cooldown {c.Risk.LossCooldownMinutes}m")}");
        Console.WriteLine($"  Break-even    : {(c.Trading.BreakEvenEnabled ? "on" : "off")} · trail at {c.Trading.TrailStartAtR}R · scratch {c.Trading.ScratchUnprofitableMinutes}m{(c.Trading.ScratchOnlyAboveR > -1 ? $" if R > {c.Trading.ScratchOnlyAboveR}" : "")}");
        Console.WriteLine($"  Pullback trig : {(c.Signals.PullbackTriggerEnabled ? $"on (vol ≥ {c.Signals.PullbackMinVolumeRatio}x)" : "off")}");
        Console.WriteLine($"  Session state : {(c.Risk.PersistSessionState ? c.Risk.SessionStatePath : "off")}");
        Console.WriteLine($"  Min score     : {c.Signals.MinScore:F2} with {c.Signals.MinConfirmations}+ confirmations" +
                          (c.Signals.MinDirectionEdge > 0 ? $" · edge ≥ {c.Signals.MinDirectionEdge:F2}" : ""));
        Console.WriteLine($"  Loop          : every {c.Trading.LoopIntervalMs}ms, status every {c.Trading.StatusLogEveryTicks} ticks");
        Console.WriteLine($"  MACD align    : {(c.Signals.RequireMacdAlign ? "required" : "optional")}");
        Console.WriteLine($"  HTF flat      : {(c.Signals.RequireDecidedHtf ? "blocks entries" : "allows chop setups")}");
        Console.WriteLine($"  Stand-aside   : {(c.Signals.StandAsideEnabled ? $"funding {c.Signals.MaxFundingRate:P2} / {c.Signals.AnchorMarket} {c.Signals.AnchorInterval} flip" : "off")}");
        Console.WriteLine("  Ctrl+C to stop.");
        Console.WriteLine();
    }
}
