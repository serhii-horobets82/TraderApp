using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Exchange;

/// <summary>
/// Maintains Bybit linear 50-level books locally. The stream is an optimisation,
/// never a source of authority: consumers fall back to REST when a book is not
/// available or becomes stale.
/// </summary>
internal sealed class BybitPublicOrderBookStream : IDisposable
{
    private readonly BybitConfig _cfg;
    private readonly string[] _markets;
    private readonly Dictionary<string, LiveBook> _books;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private bool _disposed;

    public BybitPublicOrderBookStream(BybitConfig cfg)
    {
        _cfg = cfg;
        _markets = cfg.Markets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _books = _markets.ToDictionary(m => m, _ => new LiveBook(), StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetFresh(string market, int limit, out OrderBook? book)
    {
        EnsureStarted();
        book = null;
        return limit <= 50 && _books.TryGetValue(market, out var live) &&
               live.TrySnapshot(limit, TimeSpan.FromMilliseconds(_cfg.PublicOrderBookFreshnessMs), out book);
    }

    public void SeedRestSnapshot(string market, OrderBook book)
    {
        if (_books.TryGetValue(market, out var live)) live.SeedRestSnapshot(book);
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed || _runTask is not null || _markets.Length == 0) return;
            _runTask = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var reconnectDelay = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                lock (_gate) _socket = socket;
                await socket.ConnectAsync(BuildPublicStreamUri(_cfg.BaseUrl, _cfg.Category), _stop.Token);
                await SendAsync(socket, new { op = "subscribe", args = _markets.Select(m => $"orderbook.50.{m}").ToArray() }, _stop.Token);
                Log.Info($"Bybit public book stream connected ({_markets.Length} market(s), 50 levels)");
                reconnectDelay = TimeSpan.FromSeconds(1);

                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var keepAlive = KeepAliveAsync(socket, connectionStop.Token);
                try { await ReceiveUntilDisconnectedAsync(socket, connectionStop.Token); }
                finally
                {
                    connectionStop.Cancel();
                    try { await keepAlive; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Bybit public book stream disconnected: {ex.Message}; REST books remain active");
            }
            finally
            {
                lock (_gate) _socket = null;
            }

            try { await Task.Delay(reconnectDelay, _stop.Token); }
            catch (OperationCanceledException) { break; }
            reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveUntilDisconnectedAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var root = JObject.Parse(await ReceiveTextAsync(socket, ct));
            var topic = root.Value<string>("topic");
            if (topic is null || !topic.StartsWith("orderbook.50.", StringComparison.Ordinal)) continue;
            var market = topic["orderbook.50.".Length..];
            if (_books.TryGetValue(market, out var book)) book.Apply(root);
        }
    }

    private static async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            await SendAsync(socket, new { op = "ping" }, ct);
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var body = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Bybit closed the public book stream.");
            body.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    internal static Uri BuildPublicStreamUri(string baseUrl, string category)
    {
        var host = new Uri(baseUrl).Host;
        var streamHost = host.Equals("api-testnet.bybit.com", StringComparison.OrdinalIgnoreCase)
            ? "stream-testnet.bybit.com"
            : "stream.bybit.com";
        return new Uri($"wss://{streamHost}/v5/public/{category}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            try { _socket?.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed class LiveBook
    {
        private readonly object _gate = new();
        private readonly SortedDictionary<decimal, decimal> _bids = new(Comparer<decimal>.Create((x, y) => y.CompareTo(x)));
        private readonly SortedDictionary<decimal, decimal> _asks = new();
        private DateTime _receivedUtc;
        private bool _hasStreamSnapshot;

        public void Apply(JObject root)
        {
            var data = root["data"] as JObject;
            var type = root.Value<string>("type");
            if (data is null || type is null) return;

            lock (_gate)
            {
                if (type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    _bids.Clear();
                    _asks.Clear();
                    ApplySide(_bids, data["b"] as JArray);
                    ApplySide(_asks, data["a"] as JArray);
                    _hasStreamSnapshot = true;
                }
                else if (type.Equals("delta", StringComparison.OrdinalIgnoreCase) && _hasStreamSnapshot)
                {
                    ApplySide(_bids, data["b"] as JArray);
                    ApplySide(_asks, data["a"] as JArray);
                }
                else return;

                _receivedUtc = DateTime.UtcNow;
            }
        }

        public void SeedRestSnapshot(OrderBook snapshot)
        {
            lock (_gate)
            {
                if (_hasStreamSnapshot) return;
                _bids.Clear();
                _asks.Clear();
                foreach (var row in snapshot.Bids.Where(r => r.Count >= 2 && r[0] > 0 && r[1] > 0)) _bids[row[0]] = row[1];
                foreach (var row in snapshot.Asks.Where(r => r.Count >= 2 && r[0] > 0 && r[1] > 0)) _asks[row[0]] = row[1];
                _receivedUtc = snapshot.ReceivedAtUtc == DateTime.MinValue ? DateTime.UtcNow : snapshot.ReceivedAtUtc;
            }
        }

        public bool TrySnapshot(int limit, TimeSpan maxAge, out OrderBook? snapshot)
        {
            lock (_gate)
            {
                snapshot = null;
                if (_receivedUtc == DateTime.MinValue || DateTime.UtcNow - _receivedUtc > maxAge ||
                    _bids.Count == 0 || _asks.Count == 0)
                    return false;

                snapshot = new OrderBook
                {
                    Timestamp = new DateTimeOffset(_receivedUtc).ToUnixTimeMilliseconds(),
                    ReceivedAtUtc = _receivedUtc,
                    Bids = _bids.Take(limit).Select(x => new List<decimal> { x.Key, x.Value }).ToList(),
                    Asks = _asks.Take(limit).Select(x => new List<decimal> { x.Key, x.Value }).ToList()
                };
                return true;
            }
        }

        private static void ApplySide(SortedDictionary<decimal, decimal> side, JArray? rows)
        {
            foreach (var row in rows ?? new JArray())
            {
                if (row is not JArray pair || pair.Count < 2 ||
                    !decimal.TryParse(pair[0]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    !decimal.TryParse(pair[1]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var quantity) ||
                    price <= 0) continue;
                if (quantity <= 0) side.Remove(price); else side[price] = quantity;
            }
        }
    }
}
