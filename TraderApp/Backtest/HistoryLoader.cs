using Newtonsoft.Json;
using TraderApp.Exchange;
using TraderApp.Indicators;
using TraderApp.Trading;

namespace TraderApp.Backtest;

/// <summary>
/// Fetches and caches historical klines.
///
/// The v1 kline endpoint returns at most 1440 bars per call, so longer ranges
/// are paged with start/end. Results are cached on disk because a parameter
/// sweep re-reads the same history hundreds of times and the endpoint is rate
/// limited to 1000 requests / 10s.
/// </summary>
public sealed class HistoryLoader
{
    private readonly WhiteBitClient _client;
    private readonly string _cacheDir;

    public HistoryLoader(WhiteBitClient client, string cacheDir)
    {
        _client = client;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<IReadOnlyList<Candle>> LoadAsync(
        string market, string interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var cacheFile = Path.Combine(_cacheDir,
            $"{market}_{interval}_{fromUtc:yyyyMMdd}_{toUtc:yyyyMMdd}.json");

        if (File.Exists(cacheFile))
        {
            var cached = JsonConvert.DeserializeObject<List<Candle>>(await File.ReadAllTextAsync(cacheFile, ct));
            if (cached is { Count: > 0 }) return cached;
        }

        var barLength = WhiteBitClient.ParseInterval(interval);
        if (barLength <= TimeSpan.Zero) throw new ArgumentException($"bad interval '{interval}'");

        var all = new SortedDictionary<DateTime, Candle>();
        var cursor = fromUtc;

        while (cursor < toUtc && !ct.IsCancellationRequested)
        {
            var chunkEnd = cursor + barLength * 1440;
            if (chunkEnd > toUtc) chunkEnd = toUtc;

            var page = await _client.GetKlinesRangeAsync(market, interval, cursor, chunkEnd, ct);
            if (page.Count == 0) break;

            foreach (var c in page) all[c.OpenTime] = c;

            var newest = page[^1].OpenTime;
            if (newest + barLength <= cursor) break;   // no forward progress
            cursor = newest + barLength;

            await Task.Delay(120, ct);                 // stay well inside the rate limit
        }

        var result = all.Values.Where(c => c.OpenTime >= fromUtc && c.OpenTime < toUtc).ToList();
        await File.WriteAllTextAsync(cacheFile, JsonConvert.SerializeObject(result), ct);
        Log.Info($"  {market} {interval}: {result.Count} bars " +
                 $"{(result.Count > 0 ? $"{result[0].OpenTime:yyyy-MM-dd HH:mm} .. {result[^1].OpenTime:yyyy-MM-dd HH:mm}" : "")}");
        return result;
    }
}
