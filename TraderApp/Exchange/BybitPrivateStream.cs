using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Exchange;

/// <summary>
/// Small, deliberately passive Bybit private-stream client. It does not mutate
/// local order state: it merely wakes the normal REST reconciliation loop as
/// soon as Bybit reports an order, execution, or position update.
/// </summary>
internal sealed class BybitPrivateStream : IDisposable
{
    private readonly BybitConfig _cfg;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _eventSignal = new(0, 1);
    private readonly object _startGate = new();
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private bool _disposed;

    public BybitPrivateStream(BybitConfig cfg) => _cfg = cfg;

    public async Task WaitForEventOrDelayAsync(TimeSpan maxDelay, CancellationToken ct)
    {
        EnsureStarted();
        // Do not leave a pending semaphore wait behind when the normal polling
        // delay wins; an abandoned waiter would consume the next event and
        // make a later loop iteration sleep unnecessarily.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(maxDelay);
        try
        {
            await _eventSignal.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Expected max-delay expiry. The caller performs REST reconciliation.
        }
    }

    private void EnsureStarted()
    {
        lock (_startGate)
        {
            if (_disposed || _runTask is not null) return;
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
                lock (_startGate) _socket = socket;
                await socket.ConnectAsync(BuildPrivateStreamUri(_cfg.BaseUrl), _stop.Token);
                await AuthenticateAndSubscribeAsync(socket, _stop.Token);
                Log.Info("Bybit private stream connected (order/execution/position wakeups enabled)");
                reconnectDelay = TimeSpan.FromSeconds(1);
                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var keepAlive = KeepAliveAsync(socket, connectionStop.Token);
                try
                {
                    await ReceiveUntilDisconnectedAsync(socket, connectionStop.Token);
                }
                finally
                {
                    connectionStop.Cancel();
                    try { await keepAlive; }
                    catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Bybit private stream disconnected: {ex.Message}; REST polling remains active");
            }
            finally
            {
                lock (_startGate) _socket = null;
            }

            try { await Task.Delay(reconnectDelay, _stop.Token); }
            catch (OperationCanceledException) { break; }
            reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 30));
        }
    }

    private async Task AuthenticateAndSubscribeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds();
        var signature = Sign($"GET/realtime{expires}");
        await SendAsync(socket, new { op = "auth", args = new object[] { _cfg.Key, expires, signature } }, ct);

        var authReply = await ReceiveTextAsync(socket, ct);
        var auth = JObject.Parse(authReply);
        if (auth.Value<bool?>("success") != true)
            throw new InvalidOperationException("Bybit private stream authentication failed: " +
                (auth.Value<string>("ret_msg") ?? auth.Value<string>("msg") ?? "unknown error"));

        await SendAsync(socket, new { op = "subscribe", args = new[] { "order", "execution", "position" } }, ct);
        var subscribeReply = JObject.Parse(await ReceiveTextAsync(socket, ct));
        if (subscribeReply.Value<bool?>("success") != true)
            throw new InvalidOperationException("Bybit private stream subscription failed: " +
                (subscribeReply.Value<string>("ret_msg") ?? subscribeReply.Value<string>("msg") ?? "unknown error"));
    }

    private async Task ReceiveUntilDisconnectedAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, ct);
            if (!IsAccountEvent(text)) continue;
            if (_eventSignal.CurrentCount == 0) _eventSignal.Release();
        }
    }

    private static async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        // Bybit recommends an application-level ping for long-lived streams.
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
                throw new WebSocketException("Bybit closed the private stream.");
            body.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_cfg.Secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    internal static Uri BuildPrivateStreamUri(string baseUrl)
    {
        var host = new Uri(baseUrl).Host;
        var streamHost = host.Equals("api-testnet.bybit.com", StringComparison.OrdinalIgnoreCase)
            ? "stream-testnet.bybit.com"
            : "stream.bybit.com";
        return new Uri($"wss://{streamHost}/v5/private");
    }

    internal static bool IsAccountEvent(string json)
    {
        try
        {
            var topic = JObject.Parse(json).Value<string>("topic");
            return topic is "order" or "execution" or "position";
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_startGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            try { _socket?.Abort(); } catch (ObjectDisposedException) { }
        }
        // The background receive loop is allowed to observe the cancellation
        // before these primitives are released; process shutdown follows.
    }
}
