using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using TraderApp.Configuration;

namespace TraderApp.Exchange;

public sealed class ApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string Body { get; }

    public ApiException(string endpoint, HttpStatusCode status, string body)
        : base($"{endpoint} -> HTTP {(int)status}: {Truncate(body)}")
    {
        Status = status;
        Body = body;
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "...";
}

/// <summary>
/// WhiteBIT REST client.
///
/// Fixes over the original implementation:
///  - nonces are strictly monotonic and serialised, so two requests in the same
///    millisecond cannot collide;
///  - private requests are paced and retried with exponential backoff + jitter
///    on 429 and 5xx, per the documented guidance;
///  - the HttpClient has an explicit timeout instead of the 100s default;
///  - non-2xx responses throw instead of silently returning default(T), so a
///    failed cancel can never be mistaken for a successful one.
/// </summary>
public sealed class WhiteBitClient : IDisposable
{
    /// <summary>
    /// All responses are read with <see cref="LenientDecimalConverter"/>: WhiteBIT
    /// returns null for numeric fields that are not yet meaningful, and a throw
    /// there kills the whole trading cycle.
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Converters = { new LenientDecimalConverter() }
    };

    private readonly ApiConfig _cfg;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _privateGate = new(1, 1);
    private long _lastNonce;
    private long _lastPrivateTicks;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Indicators.Candle> Candles, DateTime ValidUntilUtc)> _klineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<Indicators.Candle>>> _klineInflight = new(StringComparer.OrdinalIgnoreCase);

    public WhiteBitClient(ApiConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient
        {
            BaseAddress = new Uri(cfg.BaseUrl),
            Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "TraderApp/2.0");
    }

    // ── Public endpoints ────────────────────────────────────────────────

    /// <summary>GET /api/v4/public/markets - market list including tickSize/stepSize.</summary>
    public async Task<IReadOnlyDictionary<string, MarketRules>> GetMarketRulesAsync(CancellationToken ct)
    {
        var list = await GetAsync<List<MarketInfo>>("/api/v4/public/markets", ct)
                   ?? new List<MarketInfo>();
        return list
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => new MarketRules(g.First()), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>GET /api/v4/public/orderbook/{market}</summary>
    public Task<OrderBook?> GetOrderBookAsync(string market, int limit, CancellationToken ct) =>
        GetAsync<OrderBook>($"/api/v4/public/orderbook/{market}?limit={Math.Clamp(limit, 1, 100)}", ct);

    /// <summary>
    /// GET /api/v1/public/kline - still the v1 endpoint; there is no v4 equivalent.
    /// Element order is [timestamp, open, close, high, low, volumeStock, volumeMoney]
    /// despite what an OHLC reading of the docs sample suggests, so the parsing is
    /// done explicitly in <see cref="Candle.FromKline"/>.
    ///
    /// Closed bars do not change until the next interval elapses, so the live
    /// loop reuses the last fetch until that close rather than downloading 15m
    /// and 1h history every few seconds.
    /// </summary>
    public Task<IReadOnlyList<Indicators.Candle>> GetKlinesAsync(
        string market, string interval, int limit, CancellationToken ct, bool closedOnly = true)
    {
        var clamped = Math.Clamp(limit, 1, 1440);
        var key = $"{market}|{interval}|{clamped}|{(closedOnly ? "c" : "o")}";
        if (_klineCache.TryGetValue(key, out var hit) && DateTime.UtcNow < hit.ValidUntilUtc)
            return Task.FromResult(hit.Candles);

        var task = _klineInflight.GetOrAdd(key, _ =>
            FetchKlinesUncachedAsync(key, market, interval, clamped, closedOnly, ct));

        return AwaitKlinesAsync(key, task);
    }

    private async Task<IReadOnlyList<Indicators.Candle>> AwaitKlinesAsync(
        string key, Task<IReadOnlyList<Indicators.Candle>> task)
    {
        try { return await task; }
        finally
        {
            if (_klineInflight.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                _klineInflight.TryRemove(key, out _);
        }
    }

    private async Task<IReadOnlyList<Indicators.Candle>> FetchKlinesUncachedAsync(
        string cacheKey, string market, string interval, int limit, bool closedOnly, CancellationToken ct)
    {
        var url = $"/api/v1/public/kline?market={market}&interval={interval}&limit={limit}";
        var raw = await GetAsync<KlineEnvelope>(url, ct);
        if (raw is null || !raw.Success || raw.Result is null)
            return Array.Empty<Indicators.Candle>();

        var candles = new List<Indicators.Candle>(raw.Result.Count);
        foreach (var row in raw.Result)
        {
            var c = Indicators.Candle.FromKline(row);
            if (c is not null) candles.Add(c);
        }
        candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));

        // The most recent kline is the bar currently forming: its close, volume
        // and therefore every indicator built on it change until the bar ends.
        // Acting on it means acting on a value that repaints, and it is why the
        // volume component reads ~0x on a freshly opened bar. Drop it.
        var duration = ParseInterval(interval);
        if (closedOnly && candles.Count > 0 && duration > TimeSpan.Zero &&
            candles[^1].OpenTime + duration > DateTime.UtcNow)
            candles.RemoveAt(candles.Count - 1);

        if (candles.Count > 0)
        {
            var validUntil = closedOnly && duration > TimeSpan.Zero
                ? candles[^1].OpenTime + duration
                : DateTime.UtcNow.AddSeconds(5);
            if (validUntil > DateTime.UtcNow)
                _klineCache[cacheKey] = (candles, validUntil);
        }

        return candles;
    }

    /// <summary>
    /// GET /api/v1/public/kline over an explicit time range, for backtesting.
    /// Unlike the live path this keeps the still-forming final bar out by range,
    /// not by wall clock, so a replay sees exactly the bars that had closed.
    /// </summary>
    public async Task<IReadOnlyList<Indicators.Candle>> GetKlinesRangeAsync(
        string market, string interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var url = $"/api/v1/public/kline?market={market}&interval={interval}" +
                  $"&start={start}&end={end}&limit=1440";

        var raw = await GetAsync<KlineEnvelope>(url, ct);
        if (raw is null || !raw.Success || raw.Result is null) return Array.Empty<Indicators.Candle>();

        var candles = new List<Indicators.Candle>(raw.Result.Count);
        foreach (var row in raw.Result)
        {
            var c = Indicators.Candle.FromKline(row);
            if (c is not null) candles.Add(c);
        }
        candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return candles;
    }

    /// <summary>Converts a WhiteBIT kline interval (1s, 5m, 4h, 1d, 1w, 1M) to a duration.</summary>
    internal static TimeSpan ParseInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval) || interval.Length < 2) return TimeSpan.Zero;

        var unit = interval[^1];
        if (!int.TryParse(interval[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return TimeSpan.Zero;

        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(7 * n),
            'M' => TimeSpan.FromDays(30 * n),
            _ => TimeSpan.Zero
        };
    }

    private sealed class KlineEnvelope
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("message")] public object? Message { get; set; }
        [JsonProperty("result")] public List<List<object>>? Result { get; set; }
    }

    // ── Private endpoints ───────────────────────────────────────────────

    /// <summary>POST /api/v4/collateral-account/summary</summary>
    public Task<CollateralSummary?> GetCollateralSummaryAsync(CancellationToken ct) =>
        PostAsync<CollateralSummary>("/api/v4/collateral-account/summary", new Payload(), ct);

    /// <summary>POST /api/v4/collateral-account/positions/open</summary>
    public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(string? market, CancellationToken ct)
    {
        var res = await PostAsync<List<Position>>(
            "/api/v4/collateral-account/positions/open",
            new MarketPayload { Market = market }, ct);
        return res ?? new List<Position>();
    }

    /// <summary>POST /api/v4/collateral-account/positions/closed-pnl - realised results, newest first.</summary>
    public async Task<IReadOnlyList<ClosedPnl>> GetClosedPnlAsync(int limit, CancellationToken ct)
    {
        var res = await PostAsync<List<ClosedPnl>>(
            "/api/v4/collateral-account/positions/closed-pnl",
            new LimitPayload { Limit = Math.Clamp(limit, 1, 100) }, ct);
        return res ?? new List<ClosedPnl>();
    }

    /// <summary>POST /api/v4/market/fee/single - this account's real fee for one market.</summary>
    public Task<MarketFee?> GetMarketFeeAsync(string market, CancellationToken ct) =>
        PostAsync<MarketFee>("/api/v4/market/fee/single",
            new MarketPayload { Market = market }, ct);

    /// <summary>POST /api/v4/collateral-account/leverage</summary>
    public Task<object?> SetLeverageAsync(int leverage, CancellationToken ct) =>
        PostAsync<object>("/api/v4/collateral-account/leverage",
            new LeveragePayload { Leverage = leverage }, ct);

    /// <summary>POST /api/v4/order/collateral/limit</summary>
    public Task<OrderResponse?> PlaceCollateralLimitAsync(CollateralLimitOrderPayload p, CancellationToken ct) =>
        PostAsync<OrderResponse>("/api/v4/order/collateral/limit", p, ct);

    /// <summary>POST /api/v4/order/collateral/market</summary>
    public Task<OrderResponse?> PlaceCollateralMarketAsync(CollateralMarketOrderPayload p, CancellationToken ct) =>
        PostAsync<OrderResponse>("/api/v4/order/collateral/market", p, ct);

    /// <summary>
    /// POST /api/v4/collateral-account/position/close - closes at market and
    /// cancels any attached TP/SL. Preferred over a reduce-only market order
    /// when the intent is "flatten this position completely".
    /// </summary>
    public Task<object?> ClosePositionAsync(long positionId, string market, string? positionSide, CancellationToken ct) =>
        PostAsync<object>("/api/v4/collateral-account/position/close",
            new ClosePositionPayload { PositionId = positionId, Market = market, PositionSide = positionSide }, ct);

    /// <summary>POST /api/v4/order/cancel</summary>
    public Task<OrderResponse?> CancelOrderAsync(string market, long orderId, CancellationToken ct) =>
        PostAsync<OrderResponse>("/api/v4/order/cancel",
            new CancelOrderPayload { Market = market, OrderId = orderId }, ct);

    /// <summary>POST /api/v4/orders - unexecuted (still open) orders for a market.</summary>
    public async Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string market, CancellationToken ct)
    {
        var res = await PostAsync<List<OrderResponse>>("/api/v4/orders",
            new MarketPayload { Market = market }, ct);
        return res ?? new List<OrderResponse>();
    }

    // ── Transport ───────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(path, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<T>(body, JsonSettings);

                if (!ShouldRetry(resp.StatusCode) || attempt >= _cfg.MaxRetries)
                    throw new ApiException(path, resp.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= _cfg.MaxRetries) throw;
            }

            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    private async Task<T?> PostAsync<T>(string endpoint, Payload payload, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpStatusCode status;
            string body;

            await _privateGate.WaitAsync(ct);
            try
            {
                await PaceAsync(ct);

                payload.Request = endpoint;
                payload.NonceWindow = false;
                payload.Nonce = NextNonce();

                var json = JsonConvert.SerializeObject(payload);
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                var signature = Sign(encoded, _cfg.Secret);

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("X-TXC-APIKEY", _cfg.Key);
                req.Headers.Add("X-TXC-PAYLOAD", encoded);
                req.Headers.Add("X-TXC-SIGNATURE", signature);

                using var resp = await _http.SendAsync(req, ct);
                status = resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                _privateGate.Release();
            }

            if (status == HttpStatusCode.OK)
                return string.IsNullOrWhiteSpace(body) ? default : JsonConvert.DeserializeObject<T>(body, JsonSettings);

            if (!ShouldRetry(status) || attempt >= _cfg.MaxRetries)
                throw new ApiException(endpoint, status, body);

            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    private static bool ShouldRetry(HttpStatusCode s) =>
        s == HttpStatusCode.TooManyRequests || (int)s >= 500;

    /// <summary>Exponential backoff starting at 1s, doubling, capped at 30s, with jitter.</summary>
    private static TimeSpan BackoffDelay(int attempt)
    {
        var baseMs = Math.Min(30_000d, 1_000d * Math.Pow(2, attempt));
        var jitter = Random.Shared.NextDouble() * 0.3 * baseMs;
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    /// <summary>Spread private requests out instead of bursting them.</summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        var minGap = TimeSpan.FromMilliseconds(_cfg.MinPrivateRequestGapMs).Ticks;
        var elapsed = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPrivateTicks);
        if (elapsed < minGap)
            await Task.Delay(TimeSpan.FromTicks(minGap - elapsed), ct);
        Interlocked.Exchange(ref _lastPrivateTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Strictly increasing millisecond nonce. The original implementation used
    /// the raw clock, so two requests inside the same millisecond produced a
    /// duplicate nonce and the second was rejected.
    /// </summary>
    private string NextNonce()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long next;
        long prev;
        do
        {
            prev = Interlocked.Read(ref _lastNonce);
            next = now > prev ? now : prev + 1;
        } while (Interlocked.CompareExchange(ref _lastNonce, next, prev) != prev);

        return next.ToString(CultureInfo.InvariantCulture);
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        _http.Dispose();
        _privateGate.Dispose();
    }
}
