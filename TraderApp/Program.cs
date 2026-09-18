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
            errors = errors.Where(e => !e.StartsWith("Api.", StringComparison.Ordinal)).ToList();
        }
        if (errors.Count > 0)
        {
            foreach (var e in errors) Log.Error(e);
            Log.Error("configuration is invalid - aborting");
            return 1;
        }

        // Default is live. appsettings DryRun is ignored; only these flags opt out.
        config.Trading.DryRun = dryRun || signalsOnly;
        config.Trading.IUnderstandThisTradesRealMoney = true;
        var live = !signalsOnly && !dryRun;

        PrintBanner(config, live, signalsOnly);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Warn("shutdown requested - finishing the current cycle");
            cts.Cancel();
        };

        using var client = new WhiteBitClient(config.Api);

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

        try
        {
            if (signalsOnly)
            {
                await new SignalMonitor(config, client).RunAsync(cts.Token);
            }
            else
            {
                await using var trader = new AutoTrader(config, client, telegram);
                await trader.RunAsync(cts.Token);
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log.Error($"fatal: {ex}");
            return 2;
        }
        finally
        {
            Log.Info("stopped");
            Log.SetNotifier(null);
            Log.Close();
        }

        return 0;
    }

    private static BotConfig LoadConfig(string[] _)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("TRADER_");

        var cfg = builder.Build().Get<BotConfig>() ?? new BotConfig();

        // Convenience aliases so the usual WhiteBIT variable names work too.
        cfg.Api.Key = FirstNonEmpty(cfg.Api.Key, Environment.GetEnvironmentVariable("WHITEBIT_API_KEY"));
        cfg.Api.Secret = FirstNonEmpty(cfg.Api.Secret, Environment.GetEnvironmentVariable("WHITEBIT_API_SECRET"));
        cfg.Telegram.BotToken = FirstNonEmpty(cfg.Telegram.BotToken, Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
        cfg.Telegram.ChatId = FirstNonEmpty(cfg.Telegram.ChatId, Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));

        // Credentials pasted through a shell or a prompt routinely pick up
        // trailing whitespace, which silently breaks HMAC signing.
        cfg.Api.Key = cfg.Api.Key.Trim();
        cfg.Api.Secret = cfg.Api.Secret.Trim();
        cfg.Telegram.BotToken = cfg.Telegram.BotToken.Trim();
        cfg.Telegram.ChatId = cfg.Telegram.ChatId.Trim();

        return cfg;
    }

    private static string FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : (b ?? "");

    private static void PrintBanner(BotConfig c, bool live, bool signalsOnly)
    {
        Console.WriteLine();
        Console.WriteLine("  TraderApp - WhiteBIT collateral scalper");
        Console.WriteLine("  ---------------------------------------");
        Console.ForegroundColor = live ? ConsoleColor.Red : ConsoleColor.Green;
        Console.WriteLine(signalsOnly
            ? "  MODE          : SIGNALS ONLY - public data, no API key used"
            : live
                ? "  MODE          : LIVE - real orders, real money"
                : "  MODE          : DRY RUN - decisions logged, nothing sent");
        Console.ResetColor();
        Console.WriteLine($"  Endpoint      : {c.Api.BaseUrl}");
        Console.WriteLine($"  Markets       : {string.Join(", ", c.Trading.Markets)}");
        Console.WriteLine($"  Timeframes    : {c.Trading.EntryInterval} entry / {c.Trading.RegimeInterval} regime");
        Console.WriteLine($"  Leverage      : {c.Trading.Leverage}x");
        Console.WriteLine($"  Risk/trade    : {c.Risk.RiskPerTradePct}% of equity");
        Console.WriteLine($"  Stop / target : {c.Trading.StopLossAtrMult}x ATR / {c.Trading.TakeProfitAtrMult}x ATR");
        Console.WriteLine($"  Hard stop     : {c.Trading.HardStopMultiplier}x bot stop, attached to the entry order");
        Console.WriteLine($"  Max positions : {c.Risk.MaxConcurrentPositions} (max {c.Risk.MaxTotalNotionalUsd} notional)");
        Console.WriteLine($"  Daily cutout  : -{c.Risk.MaxDailyDrawdownPct}% equity");
        Console.WriteLine($"  Min score     : {c.Signals.MinScore:F2} with {c.Signals.MinConfirmations}+ confirmations");
        Console.WriteLine("  Ctrl+C to stop.");
        Console.WriteLine();
    }
}
