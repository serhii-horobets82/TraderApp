using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Signals;

namespace TraderApp.Trading;

/// <summary>
/// Public-data-only mode: evaluates and prints the same signals the trader uses,
/// but touches no private endpoint and therefore needs no API key. Use it to
/// watch how the strategy behaves on your markets before handing it credentials.
/// </summary>
public sealed class SignalMonitor
{
    private readonly BotConfig _cfg;
    private readonly IExchangeClient _client;
    private readonly SignalEngine _engine;
    private readonly StandAsideReader _standAside;

    public SignalMonitor(BotConfig cfg, IExchangeClient client)
    {
        _cfg = cfg;
        _client = client;
        _engine = new SignalEngine(cfg.Signals);
        _standAside = new StandAsideReader(cfg, client);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var venueScope = Log.VenueScope(_client.ExchangeId);

        var rules = await _client.GetMarketRulesAsync(ct);
        Log.Info($"loaded rules for {rules.Count} markets");

        foreach (var m in _cfg.Trading.Markets)
        {
            if (rules.TryGetValue(m, out var r)) Log.Info($"  {r}");
            else Log.Warn($"  {m}: not listed by the exchange");
        }

        while (!ct.IsCancellationRequested)
        {
            var scans = _cfg.Trading.Markets.Select(market => ScanMarketAsync(market, ct));
            await Task.WhenAll(scans);

            try { await Task.Delay(_cfg.Trading.LoopIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanMarketAsync(string market, CancellationToken ct)
    {
        try
        {
            var depth = Math.Max(_cfg.Signals.OrderBookDepth, 20);
            var bookTask = _client.GetOrderBookAsync(market, depth, ct);
            var entryTask = _client.GetKlinesAsync(market, _cfg.Trading.EntryInterval, _cfg.Trading.CandleLookback, ct);
            var regimeTask = _client.GetKlinesAsync(market, _cfg.Trading.RegimeInterval, _cfg.Trading.CandleLookback, ct);
            var asideTask = _standAside.LoadAsync(market, ct);
            await Task.WhenAll(bookTask, entryTask, regimeTask, asideTask);

            var book = await bookTask;
            if (book is null) { Log.Warn($"{market}: no order book"); return; }

            var s = _engine.Evaluate(market, await entryTask, await regimeTask, book, await asideTask);

            var line = $"{market,-10} mid {s.Price,12} | ATR {s.AtrPct:F3}% | spread {s.SpreadBps:F2}bps | {s.Explain()}";
            if (s.IsActionable) Log.Trade(line); else Log.Info(line);

            foreach (var c in s.Components.Where(c => c.Weight > 0))
                Log.Info($"    {c.Name,-10} {c.Direction,-5} w={c.Weight:F2}  {c.Detail}");
        }
        catch (ApiException ex) { Log.Error(ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"{market}: {ex.Message}");
        }
    }
}
