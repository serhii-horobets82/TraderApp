using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TraderApp.Configuration;
using TraderApp.Indicators;
using TraderApp.Trading;

namespace TraderApp.Exchange;

/// <summary>
/// Bybit V5 REST client for USDT linear perps. Maps onto the same domain models
/// as WhiteBIT so AutoTrader can run unchanged against either venue.
/// </summary>
public sealed class BybitClient : IExchangeClient
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Converters = { new LenientDecimalConverter() }
    };

    private readonly BybitConfig _cfg;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Candle> Candles, DateTime ValidUntilUtc)> _klineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<Candle>>> _klineInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _futuresGate = new();
    private FuturesSnapshot _futuresCache = FuturesSnapshot.Empty;
    private DateTime _futuresValidUntil = DateTime.MinValue;
    private Task<FuturesSnapshot>? _futuresInflight;

    public string ExchangeId => "Bybit";

    public BybitClient(BybitConfig cfg) : this(cfg, null) { }

    /// <summary>Overload for tests: lets a fake handler stand in for the network.</summary>
    public BybitClient(BybitConfig cfg, HttpMessageHandler? handler)
    {
        _cfg = cfg;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);
        _http.DefaultRequestHeaders.Add("User-Agent", "TraderApp/2.0-Bybit");
    }

    // ── Public ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, MarketRules>> GetMarketRulesAsync(CancellationToken ct)
    {
        var path = $"/v5/market/instruments-info?category={Uri.EscapeDataString(_cfg.Category)}&limit=1000";
        var root = await GetPublicAsync(path, ct);
        var list = root?["result"]?["list"] as JArray;
        var result = new Dictionary<string, MarketRules>(StringComparer.OrdinalIgnoreCase);
        if (list is null) return result;

        foreach (var row in list)
        {
            var symbol = row.Value<string>("symbol");
            if (string.IsNullOrWhiteSpace(symbol)) continue;
            if (!string.Equals(row.Value<string>("status"), "Trading", StringComparison.OrdinalIgnoreCase))
                continue;

            var lot = row["lotSizeFilter"];
            var price = row["priceFilter"];
            var info = new MarketInfo
            {
                Name = symbol,
                Stock = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                    ? symbol[..^4] : symbol,
                Money = "USDT",
                StockPrec = DecimalsFromStep(lot?.Value<string>("qtyStep")),
                MoneyPrec = DecimalsFromStep(price?.Value<string>("tickSize")),
                MakerFee = 0.02m, // overwritten by GetMarketFeeAsync when live
                TakerFee = 0.055m,
                MinAmount = ParseDec(lot?.Value<string>("minOrderQty")),
                MinTotal = ParseDec(lot?.Value<string>("minNotionalValue")),
                MaxTotal = 0,
                TradesEnabled = true,
                IsCollateral = true, // linear perps are the Bybit trading product
                TickSize = price?.Value<string>("tickSize"),
                StepSize = lot?.Value<string>("qtyStep")
            };
            result[symbol] = new MarketRules(info);
        }

        return result;
    }

    public async Task<OrderBook?> GetOrderBookAsync(string market, int limit, CancellationToken ct)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        var path = $"/v5/market/orderbook?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}&limit={clamped}";
        var root = await GetPublicAsync(path, ct);
        var result = root?["result"];
        if (result is null) return null;

        var book = new OrderBook
        {
            TickerId = result.Value<string>("s") ?? market,
            Timestamp = result.Value<long?>("ts") ?? 0
        };

        foreach (var level in result["b"] as JArray ?? new JArray())
        {
            if (level is not JArray pair || pair.Count < 2) continue;
            book.Bids.Add(new List<decimal> { ParseDec(pair[0]?.ToString()), ParseDec(pair[1]?.ToString()) });
        }
        foreach (var level in result["a"] as JArray ?? new JArray())
        {
            if (level is not JArray pair || pair.Count < 2) continue;
            book.Asks.Add(new List<decimal> { ParseDec(pair[0]?.ToString()), ParseDec(pair[1]?.ToString()) });
        }

        return book;
    }

    public Task<FuturesSnapshot> GetFuturesSnapshotAsync(CancellationToken ct)
    {
        lock (_futuresGate)
        {
            if (DateTime.UtcNow < _futuresValidUntil)
                return Task.FromResult(_futuresCache);
            _futuresInflight ??= FetchFuturesSnapshotAsync(ct);
            return _futuresInflight;
        }
    }

    private async Task<FuturesSnapshot> FetchFuturesSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var path = $"/v5/market/tickers?category={Uri.EscapeDataString(_cfg.Category)}";
            var root = await GetPublicAsync(path, ct);
            var list = root?["result"]?["list"] as JArray;
            var rows = new List<FuturesMarket>();
            if (list is not null)
            {
                foreach (var row in list)
                {
                    var symbol = row.Value<string>("symbol");
                    if (string.IsNullOrWhiteSpace(symbol)) continue;
                    rows.Add(new FuturesMarket
                    {
                        TickerId = symbol,
                        StockCurrency = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                            ? symbol[..^4] : symbol,
                        FundingRate = ParseDec(row.Value<string>("fundingRate"))
                    });
                }
            }

            var snap = new FuturesSnapshot(rows);
            lock (_futuresGate)
            {
                _futuresCache = snap;
                _futuresValidUntil = DateTime.UtcNow.AddSeconds(60);
                _futuresInflight = null;
            }
            return snap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Diag($"[bybit futures] snapshot failed: {ex.Message}");
            lock (_futuresGate)
            {
                _futuresValidUntil = DateTime.UtcNow.AddSeconds(20);
                _futuresInflight = null;
            }
            return _futuresCache;
        }
    }

    public Task<IReadOnlyList<Candle>> GetKlinesAsync(
        string market, string interval, int limit, CancellationToken ct, bool closedOnly = true)
    {
        var clamped = Math.Clamp(limit, 1, 1000);
        var key = $"{market}|{interval}|{clamped}|{(closedOnly ? "c" : "o")}";
        if (_klineCache.TryGetValue(key, out var hit) && DateTime.UtcNow < hit.ValidUntilUtc)
            return Task.FromResult(hit.Candles);

        var task = _klineInflight.GetOrAdd(key, _ =>
            FetchKlinesUncachedAsync(key, market, interval, clamped, closedOnly, ct));
        return AwaitKlinesAsync(key, task);
    }

    private async Task<IReadOnlyList<Candle>> AwaitKlinesAsync(string key, Task<IReadOnlyList<Candle>> task)
    {
        try { return await task; }
        finally
        {
            if (_klineInflight.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                _klineInflight.TryRemove(key, out _);
        }
    }

    private async Task<IReadOnlyList<Candle>> FetchKlinesUncachedAsync(
        string cacheKey, string market, string interval, int limit, bool closedOnly, CancellationToken ct)
    {
        var bybitInterval = ToBybitInterval(interval);
        var path = $"/v5/market/kline?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}&interval={Uri.EscapeDataString(bybitInterval)}&limit={limit}";
        var root = await GetPublicAsync(path, ct);
        var list = root?["result"]?["list"] as JArray;
        var candles = ParseKlines(list);

        var duration = CandleInterval.Parse(interval);
        if (closedOnly && candles.Count > 0 && duration > TimeSpan.Zero &&
            candles[^1].OpenTime + duration > DateTime.UtcNow)
            candles.RemoveAt(candles.Count - 1);

        if (candles.Count > 0)
        {
            var now = DateTime.UtcNow;
            var validUntil = closedOnly
                ? CandleInterval.ClosedBarValidUntil(candles[^1].OpenTime, duration, now)
                : now.AddSeconds(5);
            if (validUntil > now)
                _klineCache[cacheKey] = (candles, validUntil);
        }

        return candles;
    }

    public async Task<IReadOnlyList<Candle>> GetKlinesRangeAsync(
        string market, string interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var bybitInterval = ToBybitInterval(interval);
        var start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var path = $"/v5/market/kline?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}&interval={Uri.EscapeDataString(bybitInterval)}" +
                   $"&start={start}&end={end}&limit=1000";
        var root = await GetPublicAsync(path, ct);
        return ParseKlines(root?["result"]?["list"] as JArray);
    }

    // ── Private / account ───────────────────────────────────────────────

    public async Task<CollateralSummary?> GetAccountSummaryAsync(CancellationToken ct)
    {
        var root = await GetPrivateAsync("/v5/account/wallet-balance?accountType=UNIFIED", ct);
        var list = root?["result"]?["list"] as JArray;
        var account = list?.FirstOrDefault();
        if (account is null) return null;

        var equity = ParseDec(account.Value<string>("totalEquity"));
        if (equity <= 0)
            equity = ParseDec(account.Value<string>("totalWalletBalance"));

        var coins = account["coin"] as JArray;
        var usdt = coins?.FirstOrDefault(c =>
            string.Equals(c.Value<string>("coin"), "USDT", StringComparison.OrdinalIgnoreCase));
        var free = usdt is null
            ? ParseDec(account.Value<string>("totalAvailableBalance"))
            : ParseDec(usdt.Value<string>("availableToWithdraw"));
        if (free <= 0)
            free = ParseDec(account.Value<string>("totalAvailableBalance"));

        return new CollateralSummary
        {
            Equity = equity,
            FreeMargin = free,
            Margin = Math.Max(0, equity - free),
            Leverage = 0
        };
    }

    /// <summary>Per-coin wallet balances of the unified account (same call as the summary).</summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetCollateralBalancesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var root = await GetPrivateAsync("/v5/account/wallet-balance?accountType=UNIFIED", ct);
        var account = (root?["result"]?["list"] as JArray)?.FirstOrDefault();
        if (account?["coin"] is not JArray coins) return result;

        foreach (var c in coins)
        {
            var ticker = c.Value<string>("coin");
            if (string.IsNullOrWhiteSpace(ticker)) continue;
            var bal = ParseDec(c.Value<string>("walletBalance"));
            if (bal > 0) result[ticker] = bal;
        }
        return result;
    }

    public async Task<object?> SetLeverageAsync(int leverage, CancellationToken ct)
    {
        // Bybit sets leverage per symbol. Apply to every configured market.
        object? last = null;
        var markets = _cfg.Markets.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (markets.Count == 0) return last;
        foreach (var market in markets)
        {
            var body = new JObject
            {
                ["category"] = _cfg.Category,
                ["symbol"] = market,
                ["buyLeverage"] = leverage.ToString(CultureInfo.InvariantCulture),
                ["sellLeverage"] = leverage.ToString(CultureInfo.InvariantCulture)
            };
            try { last = await PostPrivateAsync("/v5/position/set-leverage", body, ct); }
            catch (ApiException ex) when (ex.Body.Contains("leverage not modified", StringComparison.OrdinalIgnoreCase)
                                          || ex.Body.Contains("110043", StringComparison.Ordinal))
            {
                // Already at the requested leverage.
                last = new { ok = true };
            }
        }
        return last;
    }

    public async Task<MarketFee?> GetMarketFeeAsync(string market, CancellationToken ct)
    {
        var path = $"/v5/account/fee-rate?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}";
        var root = await GetPrivateAsync(path, ct);
        var row = (root?["result"]?["list"] as JArray)?.FirstOrDefault();
        if (row is null) return null;

        // Bybit returns fractions (0.0002 = 0.02%). MarketRules / sizing use percent.
        return new MarketFee
        {
            Maker = ParseDec(row.Value<string>("makerFeeRate")) * 100m,
            Taker = ParseDec(row.Value<string>("takerFeeRate")) * 100m
        };
    }

    public async Task<IReadOnlyList<Position>> GetOpenPositionsAsync(string? market, CancellationToken ct)
    {
        var path = $"/v5/position/list?category={Uri.EscapeDataString(_cfg.Category)}&settleCoin=USDT";
        if (!string.IsNullOrWhiteSpace(market))
            path += $"&symbol={Uri.EscapeDataString(market)}";

        var root = await GetPrivateAsync(path, ct);
        var list = root?["result"]?["list"] as JArray;
        var result = new List<Position>();
        if (list is null) return result;

        foreach (var row in list)
        {
            var size = ParseDec(row.Value<string>("size"));
            if (size <= 0) continue;
            var symbol = row.Value<string>("symbol") ?? "";
            var side = row.Value<string>("side") ?? "";
            var isLong = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            var signed = isLong ? size : -size;
            // createdTime is the first time this SYMBOL was ever positioned, so on
            // a reused symbol it can be days old. Only openTime describes the
            // current position, and it is 0 when Bybit does not know it.
            var opened = row.Value<long?>("openTime") ?? 0;
            result.Add(new Position
            {
                PositionId = SyntheticPositionId.For(symbol, isLong),
                Market = symbol,
                Amount = signed,
                BasePrice = ParseDec(row.Value<string>("avgPrice")),
                UnrealizedPnl = ParseDec(row.Value<string>("unrealisedPnl")),
                LiquidationPrice = ParseDec(row.Value<string>("liqPrice")),
                PositionSide = isLong ? "LONG" : "SHORT",
                OpenDate = opened > 0 ? opened / 1000.0 : 0,
                Margin = ParseDec(row.Value<string>("positionIM"))
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ClosedPnl>> GetClosedPnlAsync(int limit, CancellationToken ct)
    {
        var path = $"/v5/position/closed-pnl?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&limit={Math.Clamp(limit, 1, 100)}";
        var root = await GetPrivateAsync(path, ct);
        var list = root?["result"]?["list"] as JArray;
        var result = new List<ClosedPnl>();
        if (list is null) return result;

        foreach (var row in list)
        {
            var symbol = row.Value<string>("symbol") ?? "";
            var side = row.Value<string>("side") ?? "";
            // Closed side is the closing order side: Sell closes a long.
            var wasLong = side.Equals("Sell", StringComparison.OrdinalIgnoreCase);
            var closedPnl = ParseDec(row.Value<string>("closedPnl"));
            var openFee = ParseDec(row.Value<string>("openFee"));
            var closeFee = ParseDec(row.Value<string>("closeFee"));
            result.Add(new ClosedPnl
            {
                PositionId = SyntheticPositionId.For(symbol, wasLong),
                Market = symbol,
                GrossPnl = closedPnl + openFee + closeFee,
                NetPnl = closedPnl,
                TotalTradeFee = openFee + closeFee,
                AvgEntryPrice = ParseDec(row.Value<string>("avgEntryPrice")),
                AvgExitPrice = ParseDec(row.Value<string>("avgExitPrice")),
                ClosedSize = ParseDec(row.Value<string>("qty")),
                Side = wasLong ? "long" : "short",
                OrderId = row.Value<string>("orderId") ?? "",
                // Bybit's closed-pnl createdTime/updatedTime both stamp the closing
                // order, not the position opening. Reporting createdTime as the open
                // date made every trade look like it was held for zero seconds.
                OpenDate = 0,
                CloseDate = (row.Value<long?>("updatedTime") ?? row.Value<long?>("createdTime") ?? 0) / 1000
            });
        }

        return result;
    }

    public async Task<OrderResponse?> PlaceEntryLimitAsync(EntryLimitRequest request, CancellationToken ct)
    {
        var body = new JObject
        {
            ["category"] = _cfg.Category,
            ["symbol"] = request.Market,
            ["side"] = request.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell",
            ["orderType"] = "Limit",
            ["qty"] = request.Amount,
            ["price"] = request.Price,
            ["timeInForce"] = request.PostOnly ? "PostOnly" : "GTC",
            ["orderLinkId"] = request.ClientOrderId ?? $"ta-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        };
        if (!string.IsNullOrWhiteSpace(request.StopLoss))
            body["stopLoss"] = request.StopLoss;
        if (!string.IsNullOrWhiteSpace(request.TakeProfit))
            body["takeProfit"] = request.TakeProfit;

        var root = await PostPrivateAsync("/v5/order/create", body, ct);
        return MapOrder(root?["result"], request.Market, request.Side, request.Amount, request.Price);
    }

    public async Task<OrderResponse?> PlaceReduceOnlyMarketAsync(ReduceOnlyMarketRequest request, CancellationToken ct)
    {
        var body = new JObject
        {
            ["category"] = _cfg.Category,
            ["symbol"] = request.Market,
            ["side"] = request.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell",
            ["orderType"] = "Market",
            ["qty"] = request.Amount,
            ["reduceOnly"] = true,
            ["orderLinkId"] = request.ClientOrderId ?? $"tp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        };
        var root = await PostPrivateAsync("/v5/order/create", body, ct);
        return MapOrder(root?["result"], request.Market, request.Side, request.Amount, "0");
    }

    public async Task<object?> ClosePositionAsync(long positionId, string market, string? positionSide, CancellationToken ct)
    {
        var positions = await GetOpenPositionsAsync(market, ct);
        var pos = positions.FirstOrDefault(p => p.PositionId == positionId)
                  ?? positions.FirstOrDefault();
        if (pos is null) return new { closed = false, reason = "no position" };

        var req = new ReduceOnlyMarketRequest
        {
            Market = market,
            Side = pos.IsLong ? "sell" : "buy",
            Amount = pos.AbsAmount.ToString(CultureInfo.InvariantCulture),
            ClientOrderId = $"cl-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            PositionSide = positionSide
        };
        return await PlaceReduceOnlyMarketAsync(req, ct);
    }

    public async Task<OrderResponse?> CancelOrderAsync(string market, string orderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            throw new ArgumentException("Bybit cancel needs the order id the exchange returned", nameof(orderId));

        var body = new JObject
        {
            ["category"] = _cfg.Category,
            ["symbol"] = market,
            ["orderId"] = orderId
        };
        var root = await PostPrivateAsync("/v5/order/cancel", body, ct);
        return MapOrder(root?["result"], market, null, null, null);
    }

    public async Task<IReadOnlyList<OrderResponse>> GetOpenOrdersAsync(string market, CancellationToken ct)
    {
        var path = $"/v5/order/realtime?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}";
        var root = await GetPrivateAsync(path, ct);
        var list = root?["result"]?["list"] as JArray;
        var result = new List<OrderResponse>();
        if (list is null) return result;
        foreach (var row in list)
            result.Add(MapOrder(row, market, row.Value<string>("side"), row.Value<string>("qty"), row.Value<string>("price"))!);
        return result;
    }

    public async Task<OrderResponse?> GetOrderAsync(string market, string orderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return null;

        var path = $"/v5/order/history?category={Uri.EscapeDataString(_cfg.Category)}" +
                   $"&symbol={Uri.EscapeDataString(market)}&orderId={Uri.EscapeDataString(orderId)}&limit=1";
        var root = await GetPrivateAsync(path, ct);
        var row = (root?["result"]?["list"] as JArray)?.FirstOrDefault();
        return row is null
            ? null
            : MapOrder(row, market, row.Value<string>("side"), row.Value<string>("qty"), row.Value<string>("price"));
    }

    /// <summary>
    /// Replays up to ~500 recent executions (newest first) to find the fill
    /// that opened the position now held. See <see cref="PositionHistory"/>.
    /// </summary>
    public async Task<DateTime?> ResolvePositionOpenedAsync(Position position, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(position.Market) || position.Amount == 0) return null;

        var fills = new List<PositionFill>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var path = $"/v5/execution/list?category={Uri.EscapeDataString(_cfg.Category)}" +
                       $"&symbol={Uri.EscapeDataString(position.Market)}&limit=100";
            if (!string.IsNullOrEmpty(cursor)) path += $"&cursor={Uri.EscapeDataString(cursor)}";

            var root = await GetPrivateAsync(path, ct);
            var result = root?["result"];
            foreach (var row in result?["list"] as JArray ?? new JArray())
            {
                // Funding settlements move no size and must not count as fills.
                var execType = row.Value<string>("execType") ?? "Trade";
                if (execType.Equals("Funding", StringComparison.OrdinalIgnoreCase)) continue;

                var qty = ParseDec(row.Value<string>("execQty"));
                var ms = row.Value<long?>("execTime") ?? 0;
                if (qty <= 0 || ms <= 0) continue;

                var buy = string.Equals(row.Value<string>("side"), "Buy", StringComparison.OrdinalIgnoreCase);
                fills.Add(new PositionFill(
                    DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                    buy ? qty : -qty,
                    row.Value<long?>("seq") ?? 0));
            }

            var found = PositionHistory.FindOpenTime(position.Amount, fills);
            if (found is not null) return found;

            cursor = result?.Value<string>("nextPageCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return null;
    }

    // ── Transport ───────────────────────────────────────────────────────

    private async Task<JObject?> GetPublicAsync(string pathAndQuery, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(pathAndQuery.TrimStart('/'), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (!ShouldRetry(resp.StatusCode) || attempt >= _cfg.MaxRetries)
                        throw new ApiException(pathAndQuery, resp.StatusCode, body + HttpHint(resp.StatusCode));
                }
                else
                {
                    var root = JObject.Parse(body);
                    if (IsRateLimited(root) && attempt < _cfg.MaxRetries)
                    {
                        await Task.Delay(RateLimitDelay(resp, attempt), ct);
                        continue;
                    }
                    EnsureOk(pathAndQuery, root, body);
                    return root;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= _cfg.MaxRetries) throw;
            }

            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    /// <summary>
    /// Bybit reports rate limiting as HTTP 200 with retCode 10006, so the
    /// HTTP-status retry path never sees it. Without this, one throttled call
    /// surfaced as an exception and cost the whole entry scan for that tick.
    /// </summary>
    private static bool IsRateLimited(JObject root) => root.Value<int?>("retCode") == 10006;

    /// <summary>
    /// Wait until the window resets when Bybit says when that is
    /// (X-Bapi-Limit-Reset-Timestamp, ms epoch), otherwise plain backoff.
    /// Capped so a bogus header cannot stall a tick for long.
    /// </summary>
    private static TimeSpan RateLimitDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.TryGetValues("X-Bapi-Limit-Reset-Timestamp", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetMs))
        {
            var wait = DateTimeOffset.FromUnixTimeMilliseconds(resetMs) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return TimeSpan.FromMilliseconds(Math.Min(5_000, wait.TotalMilliseconds + 100));
        }
        return BackoffDelay(attempt);
    }

    private async Task<JObject?> GetPrivateAsync(string pathAndQuery, CancellationToken ct)
    {
        var qIdx = pathAndQuery.IndexOf('?');
        var path = qIdx >= 0 ? pathAndQuery[..qIdx] : pathAndQuery;
        var query = qIdx >= 0 ? pathAndQuery[(qIdx + 1)..] : "";

        for (var attempt = 0; ; attempt++)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var recv = _cfg.RecvWindowMs.ToString(CultureInfo.InvariantCulture);
            var signPayload = timestamp + _cfg.Key + recv + query;
            var sign = Sign(signPayload, _cfg.Secret);

            using var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery.TrimStart('/'));
            AddAuthHeaders(req, timestamp, sign, recv);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var root = JObject.Parse(body);
                if (IsRateLimited(root) && attempt < _cfg.MaxRetries)
                {
                    await Task.Delay(RateLimitDelay(resp, attempt), ct);
                    continue;
                }
                EnsureOk(path, root, body);
                return root;
            }

            if (!ShouldRetry(resp.StatusCode) || attempt >= _cfg.MaxRetries)
                throw new ApiException(path, resp.StatusCode, body);
            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    private async Task<JObject?> PostPrivateAsync(string path, JObject payload, CancellationToken ct)
    {
        var json = payload.ToString(Formatting.None);
        for (var attempt = 0; ; attempt++)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var recv = _cfg.RecvWindowMs.ToString(CultureInfo.InvariantCulture);
            var signPayload = timestamp + _cfg.Key + recv + json;
            var sign = Sign(signPayload, _cfg.Secret);

            using var req = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req, timestamp, sign, recv);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var root = JObject.Parse(body);
                if (IsRateLimited(root) && attempt < _cfg.MaxRetries)
                {
                    await Task.Delay(RateLimitDelay(resp, attempt), ct);
                    continue;
                }
                EnsureOk(path, root, body);
                return root;
            }

            if (!ShouldRetry(resp.StatusCode) || attempt >= _cfg.MaxRetries)
                throw new ApiException(path, resp.StatusCode, body);
            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    private void AddAuthHeaders(HttpRequestMessage req, string timestamp, string sign, string recv)
    {
        req.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", _cfg.Key);
        req.Headers.TryAddWithoutValidation("X-BAPI-SIGN", sign);
        req.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", timestamp);
        req.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", recv);
        req.Headers.TryAddWithoutValidation("X-BAPI-SIGN-TYPE", "2");
    }

    private static void EnsureOk(string endpoint, JObject root, string body)
    {
        var code = root.Value<int?>("retCode") ?? -1;
        if (code == 0) return;
        var msg = root.Value<string>("retMsg") ?? "unknown";
        throw new ApiException(endpoint, HttpStatusCode.BadRequest,
            $"retCode={code} {msg}{HintFor(code)} | {Truncate(body)}");
    }

    /// <summary>
    /// Bybit's retCodes are numeric and the messages terse. These are the ones
    /// that look like a broken client but are really account or hosting
    /// configuration, and each cost real time to work out from the code alone.
    /// </summary>
    private static string HintFor(int code) => code switch
    {
        10003 => " — API key invalid. Check BYBIT_API_KEY, and that the key is for api.bybit.com and not a demo/testnet key.",
        10004 => " — signature failed. Usually a wrong secret, or stray whitespace in it.",
        10005 => " — permission denied. The key needs Contract → Orders & Positions (and Unified Trading → Trade on a UTA).",
        10006 => " — rate limited.",
        10010 => " — request IP not in the key's whitelist. Railway Hobby has no static egress IP, so either remove the IP binding (key then expires every 90 days) or move to a plan with static IPs.",
        10016 => " — Bybit service error on their side, not yours.",
        33004 => " — API key expired. Unbound keys expire 90 days after creation; renew it in the Bybit UI.",
        _ => ""
    };

    /// <summary>
    /// Bybit answers 403 to requests from US and Mainland China IP addresses.
    /// On a PaaS that is a hosting-region problem, not a credentials problem,
    /// and the raw body does not say so.
    /// </summary>
    private static string HttpHint(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden =>
            "  [hint] Bybit blocks US and Mainland China IPs with 403. If this runs on Railway/Render, " +
            "check the service region - the default is US. Deploy to an EU/Asia region, or use the " +
            "regional Bybit endpoint for where you are hosted.",
        HttpStatusCode.Unauthorized =>
            "  [hint] key rejected. Verify BYBIT_API_KEY/SECRET and that they are mainnet, not testnet.",
        _ => ""
    };

    private static bool ShouldRetry(HttpStatusCode s) =>
        s == HttpStatusCode.TooManyRequests || (int)s >= 500;

    private static TimeSpan BackoffDelay(int attempt)
    {
        var baseMs = Math.Min(30_000d, 1_000d * Math.Pow(2, attempt));
        var jitter = Random.Shared.NextDouble() * 0.3 * baseMs;
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static List<Candle> ParseKlines(JArray? list)
    {
        var candles = new List<Candle>();
        if (list is null) return candles;

        // Bybit returns newest-first: [start, open, high, low, close, volume, turnover]
        foreach (var row in list)
        {
            if (row is not JArray cells || cells.Count < 6) continue;
            if (!long.TryParse(cells[0]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                continue;
            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                ParseDec(cells[1]?.ToString()),
                ParseDec(cells[2]?.ToString()),
                ParseDec(cells[3]?.ToString()),
                ParseDec(cells[4]?.ToString()),
                ParseDec(cells[5]?.ToString()),
                cells.Count > 6 ? ParseDec(cells[6]?.ToString()) : 0m));
        }

        candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return candles;
    }

    private static OrderResponse? MapOrder(JToken? row, string? market, string? side, string? amount, string? price)
    {
        if (row is null) return null;
        return new OrderResponse
        {
            // Bybit order ids are UUID-like strings. Parsing them as integers
            // collapsed every id to 0, so cancels hit "order not exists" and
            // pending entries could not be told apart.
            OrderId = row.Value<string>("orderId") ?? "",
            ClientOrderId = row.Value<string>("orderLinkId"),
            Market = row.Value<string>("symbol") ?? market,
            Side = side,
            Amount = ParseDec(amount ?? row.Value<string>("qty")),
            Left = ParseDec(row.Value<string>("leavesQty") ?? amount),
            DealStock = ParseDec(row.Value<string>("cumExecQty")),
            Price = ParseDec(price ?? row.Value<string>("price")),
            Status = row.Value<string>("orderStatus") ?? "New",
            ReduceOnly = row.Value<bool?>("reduceOnly") ?? false
        };
    }

    /// <summary>Map bot intervals (15m, 1h) to Bybit V5 interval codes (15, 60).</summary>
    public static string ToBybitInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval)) return "15";
        var unit = interval[^1];
        if (!int.TryParse(interval[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return interval;
        return unit switch
        {
            'm' => n.ToString(CultureInfo.InvariantCulture),
            'h' => (n * 60).ToString(CultureInfo.InvariantCulture),
            'd' => "D",
            'w' => "W",
            'M' => "M",
            _ => interval
        };
    }

    private static decimal ParseDec(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static int DecimalsFromStep(string? step)
    {
        if (string.IsNullOrWhiteSpace(step)) return 4;
        var s = step.TrimEnd('0');
        var dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "...";

    public void Dispose() => _http.Dispose();
}
