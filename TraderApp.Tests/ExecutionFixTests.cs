using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using TraderApp.Configuration;
using TraderApp.Exchange;
using TraderApp.Trading;

namespace TraderApp.Tests;

/// <summary>Fake Bybit transport: canned JSON per path, and a record of what was sent.</summary>
internal sealed class FakeBybitHandler : HttpMessageHandler
{
    public Dictionary<string, Queue<string>> Responses { get; } = new();
    public List<(HttpMethod Method, string PathAndQuery, string Body)> Requests { get; } = new();

    public void Enqueue(string path, string json)
    {
        if (!Responses.TryGetValue(path, out var q)) Responses[path] = q = new Queue<string>();
        q.Enqueue(json);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));

        var path = request.RequestUri.AbsolutePath;
        if (!Responses.TryGetValue(path, out var q) || q.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no fake for " + path) };

        // The last queued response repeats, so a paged call can be exercised once.
        var json = q.Count > 1 ? q.Dequeue() : q.Peek();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

public class BybitOrderIdTests
{
    private const string Uuid = "5a373bfe-188d-4913-9c81-d57ab5be8068";

    private static (BybitClient Client, FakeBybitHandler Fake) Create()
    {
        var fake = new FakeBybitHandler();
        var cfg = new BybitConfig { Key = "k", Secret = "s", MaxRetries = 0 };
        return (new BybitClient(cfg, fake), fake);
    }

    [Fact]
    public async Task Placed_order_keeps_its_string_id()
    {
        var (client, fake) = Create();
        fake.Enqueue("/v5/order/create",
            $"{{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{{\"orderId\":\"{Uuid}\",\"orderLinkId\":\"ta-1\"}}}}");

        var order = await client.PlaceEntryLimitAsync(new EntryLimitRequest
        {
            Market = "SOLUSDT", Side = "buy", Amount = "1", Price = "100", ClientOrderId = "ta-1"
        }, CancellationToken.None);

        Assert.NotNull(order);
        Assert.Equal(Uuid, order!.OrderId);
        Assert.NotEqual("0", order.OrderId);
    }

    [Fact]
    public async Task Open_orders_with_different_uuids_stay_distinct()
    {
        var (client, fake) = Create();
        fake.Enqueue("/v5/order/realtime",
            "{\"retCode\":0,\"result\":{\"list\":[" +
            "{\"orderId\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"symbol\":\"SOLUSDT\",\"qty\":\"1\",\"price\":\"1\"}," +
            "{\"orderId\":\"bbbbbbbb-0000-0000-0000-000000000002\",\"symbol\":\"SOLUSDT\",\"qty\":\"1\",\"price\":\"1\"}]}}");

        var open = await client.GetOpenOrdersAsync("SOLUSDT", CancellationToken.None);

        Assert.Equal(2, open.Select(o => o.OrderId).Distinct().Count());
    }

    [Fact]
    public async Task Cancel_sends_the_exact_string_id()
    {
        var (client, fake) = Create();
        fake.Enqueue("/v5/order/cancel",
            $"{{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{{\"orderId\":\"{Uuid}\"}}}}");

        await client.CancelOrderAsync("SOLUSDT", Uuid, CancellationToken.None);

        var sent = JObject.Parse(fake.Requests.Single(r => r.PathAndQuery.StartsWith("/v5/order/cancel")).Body);
        Assert.Equal(Uuid, sent.Value<string>("orderId"));
    }

    [Fact]
    public async Task Cancel_refuses_a_blank_id_instead_of_sending_zero()
    {
        var (client, fake) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CancelOrderAsync("SOLUSDT", "", CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Order_lookup_reports_final_status_and_filled_quantity()
    {
        var (client, fake) = Create();
        fake.Enqueue("/v5/order/history",
            $"{{\"retCode\":0,\"result\":{{\"list\":[{{\"orderId\":\"{Uuid}\",\"symbol\":\"SOLUSDT\"," +
            "\"side\":\"Buy\",\"qty\":\"2\",\"price\":\"100\",\"orderStatus\":\"Filled\",\"cumExecQty\":\"2\"," +
            "\"leavesQty\":\"0\"}]}}");

        var o = await client.GetOrderAsync("SOLUSDT", Uuid, CancellationToken.None);

        Assert.Equal("Filled", o!.Status);
        Assert.Equal(2m, o.DealStock);
    }

    [Fact]
    public void Whitebit_numeric_order_ids_become_strings()
    {
        var o = Newtonsoft.Json.JsonConvert.DeserializeObject<OrderResponse>("{\"orderId\":4180284841,\"market\":\"BTC_PERP\"}")!;
        Assert.Equal("4180284841", o.OrderId);
    }

    [Fact]
    public async Task Whitebit_cancel_rejects_a_non_numeric_id()
    {
        using var wb = new WhiteBitClient(new ApiConfig { Key = "k", Secret = "s" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            wb.CancelOrderAsync("BTC_PERP", Uuid, CancellationToken.None));
    }
}

public class BybitPositionAgeTests
{
    private static (BybitClient Client, FakeBybitHandler Fake) Create()
    {
        var fake = new FakeBybitHandler();
        return (new BybitClient(new BybitConfig { Key = "k", Secret = "s", MaxRetries = 0 }, fake), fake);
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Position_age_comes_from_openTime_never_from_createdTime()
    {
        var (client, fake) = Create();
        var firstEver = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc);
        var opened = new DateTime(2026, 10, 4, 12, 45, 6, DateTimeKind.Utc);
        fake.Enqueue("/v5/position/list",
            "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"SOLUSDT\",\"side\":\"Buy\",\"size\":\"1.5\"," +
            $"\"avgPrice\":\"150\",\"createdTime\":\"{Ms(firstEver)}\",\"openTime\":{Ms(opened)}}}]}}}}");

        var pos = Assert.Single(await client.GetOpenPositionsAsync(null, CancellationToken.None));

        Assert.Equal(opened, pos.OpenedAtUtc);
    }

    [Fact]
    public async Task Position_with_no_openTime_has_no_claimed_age()
    {
        var (client, fake) = Create();
        var firstEver = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc);
        fake.Enqueue("/v5/position/list",
            "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"SOLUSDT\",\"side\":\"Sell\",\"size\":\"1.5\"," +
            $"\"avgPrice\":\"150\",\"createdTime\":\"{Ms(firstEver)}\",\"openTime\":0}}]}}}}");

        var pos = Assert.Single(await client.GetOpenPositionsAsync(null, CancellationToken.None));

        Assert.Null(pos.OpenedAtUtc);
    }

    [Fact]
    public async Task Execution_history_finds_the_fill_that_opened_the_current_position()
    {
        var (client, fake) = Create();
        var oldClose = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var openedAt = new DateTime(2026, 10, 4, 12, 45, 6, DateTimeKind.Utc);
        string Row(string side, string qty, DateTime t, string type = "Trade") =>
            $"{{\"symbol\":\"SOLUSDT\",\"side\":\"{side}\",\"execQty\":\"{qty}\",\"execTime\":\"{Ms(t)}\",\"execType\":\"{type}\"}}";

        // Newest first, as Bybit returns it: a funding row, the entry in two
        // fills, then an older, fully closed trade on the same symbol.
        fake.Enqueue("/v5/execution/list",
            "{\"retCode\":0,\"result\":{\"nextPageCursor\":\"\",\"list\":[" +
            Row("Buy", "0", openedAt.AddHours(1), "Funding") + "," +
            Row("Buy", "0.5", openedAt.AddSeconds(1)) + "," +
            Row("Buy", "1.0", openedAt) + "," +
            Row("Sell", "2", oldClose) + "," +
            Row("Buy", "2", oldClose.AddMinutes(-30)) + "]}}");

        var pos = new Position { Market = "SOLUSDT", Amount = 1.5m, BasePrice = 150m };
        var found = await client.ResolvePositionOpenedAsync(pos, CancellationToken.None);

        Assert.Equal(openedAt, found);
    }

    [Fact]
    public async Task Closed_pnl_has_no_invented_open_date_and_carries_the_order_id()
    {
        var (client, fake) = Create();
        var closeAt = new DateTime(2026, 10, 4, 12, 45, 10, DateTimeKind.Utc);
        fake.Enqueue("/v5/position/closed-pnl",
            "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"SOLUSDT\",\"side\":\"Sell\",\"orderId\":\"abc\"," +
            "\"qty\":\"1.5\",\"closedPnl\":\"-0.0191\",\"openFee\":\"0.01\",\"closeFee\":\"0.02\"," +
            $"\"avgEntryPrice\":\"150\",\"avgExitPrice\":\"149.9\",\"createdTime\":\"{Ms(closeAt)}\",\"updatedTime\":\"{Ms(closeAt)}\"}}]}}}}");

        var rec = Assert.Single(await client.GetClosedPnlAsync(10, CancellationToken.None));

        Assert.Equal(0, rec.OpenDate);
        Assert.Equal("abc", rec.OrderId);
        Assert.Equal(new DateTimeOffset(closeAt).ToUnixTimeSeconds(), rec.CloseDate);
        Assert.Equal("long", rec.Side);
    }

    [Fact]
    public void Synthetic_position_id_is_stable_and_case_insensitive()
    {
        Assert.Equal(SyntheticPositionId.For("SOLUSDT", true), SyntheticPositionId.For("solusdt", true));
        Assert.NotEqual(SyntheticPositionId.For("SOLUSDT", true), SyntheticPositionId.For("SOLUSDT", false));
        // Fixed value: a change here would orphan every persisted position record.
        Assert.Equal(233225708L, SyntheticPositionId.For("SOLUSDT", true));
    }
}

public class PositionHistoryTests
{
    private static DateTime T(int minute) => new(2026, 10, 4, 12, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Single_fill_open()
    {
        var found = PositionHistory.FindOpenTime(2m, new[] { new PositionFill(T(5), 2m) });
        Assert.Equal(T(5), found);
    }

    [Fact]
    public void Scaled_in_entry_reports_the_first_fill()
    {
        var found = PositionHistory.FindOpenTime(3m, new[]
        {
            new PositionFill(T(7), 1m), new PositionFill(T(5), 2m)
        });
        Assert.Equal(T(5), found);
    }

    [Fact]
    public void Partial_take_does_not_move_the_opening_time()
    {
        // Bought 4 at :05, sold 2 at :20 (partial take) - still the :05 position.
        var found = PositionHistory.FindOpenTime(2m, new[]
        {
            new PositionFill(T(20), -2m), new PositionFill(T(5), 4m)
        });
        Assert.Equal(T(5), found);
    }

    [Fact]
    public void Earlier_closed_trade_on_the_same_symbol_is_not_confused_with_the_current_one()
    {
        var found = PositionHistory.FindOpenTime(2m, new[]
        {
            new PositionFill(T(40), 2m),   // current entry
            new PositionFill(T(30), -2m),  // earlier trade closed
            new PositionFill(T(10), 2m)    // earlier trade opened
        });
        Assert.Equal(T(40), found);
    }

    [Fact]
    public void Reversal_in_one_order_counts_as_a_new_position()
    {
        // Long 2, then one sell of 5 flips to short 3.
        var found = PositionHistory.FindOpenTime(-3m, new[]
        {
            new PositionFill(T(30), -5m), new PositionFill(T(10), 2m)
        });
        Assert.Equal(T(30), found);
    }

    [Fact]
    public void History_that_never_reaches_flat_yields_nothing()
    {
        var found = PositionHistory.FindOpenTime(10m, new[] { new PositionFill(T(5), 2m) });
        Assert.Null(found);
    }

    [Fact]
    public void Fills_in_the_same_millisecond_are_ordered_by_sequence()
    {
        var found = PositionHistory.FindOpenTime(2m, new[]
        {
            new PositionFill(T(5), 1m, Sequence: 11),
            new PositionFill(T(5), 1m, Sequence: 10)
        });
        Assert.Equal(T(5), found);
    }
}

public class OpenTimeRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 45, 8, DateTimeKind.Utc);

    [Fact]
    public void Plausible_time_is_accepted()
    {
        var t = Now.AddSeconds(-6);
        Assert.Equal(t, OpenTimeRules.Verify(t, Now, Now.AddSeconds(-7)));
    }

    [Fact]
    public void Time_before_this_sessions_entry_order_is_rejected()
    {
        // Symbol first positioned days ago; our order went out 6 s ago.
        var stale = Now.AddDays(-12);
        Assert.Null(OpenTimeRules.Verify(stale, Now, Now.AddSeconds(-6)));
    }

    [Fact]
    public void Future_and_missing_times_are_rejected()
    {
        Assert.Null(OpenTimeRules.Verify(Now.AddHours(1), Now, null));
        Assert.Null(OpenTimeRules.Verify(null, Now, null));
        Assert.Null(OpenTimeRules.Verify(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), Now, null));
    }

    [Fact]
    public void Small_clock_skew_in_the_future_is_clamped_to_now()
    {
        Assert.Equal(Now, OpenTimeRules.Verify(Now.AddSeconds(30), Now, null));
    }

    [Fact]
    public void Time_stop_does_not_fire_on_a_just_opened_position_once_age_is_verified()
    {
        var cfg = new TradingConfig { MaxPositionMinutes = 300, ScratchUnprofitableMinutes = 0 };
        var opened = OpenTimeRules.Verify(Now.AddSeconds(-6), Now, Now.AddSeconds(-7))!.Value;
        var state = new TestState { IsLong = true, EntryPrice = 100m, EntryAtr = 1m, OpenedUtc = opened,
                                    StopPrice = 98m, TakeProfitPrice = 105m, HighWater = 100m };

        Assert.Null(ExitRules.Decide(state, 100m, Now, cfg, 0.136m, false, false));
    }

    private sealed class TestState : ITradeState
    {
        public bool IsLong { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal EntryAtr { get; set; }
        public DateTime OpenedUtc { get; set; }
        public decimal StopPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal HighWater { get; set; }
        public bool MovedToBreakEven { get; set; }
        public decimal MaxFavourableR { get; set; }
    }
}

public class PositionStateStoreTests
{
    [Fact]
    public void Saved_state_round_trips_including_the_opening_time()
    {
        var path = Path.Combine(Path.GetTempPath(), $"positions-{Guid.NewGuid():N}.json");
        try
        {
            var opened = new DateTime(2026, 10, 4, 12, 45, 6, DateTimeKind.Utc);
            var store = new PositionStateStore(path);
            Assert.True(store.Save(new[]
            {
                new SavedPosition
                {
                    Market = "SOLUSDT", IsLong = true, OpenedUtc = opened, OpenedSource = "executions",
                    EntryPrice = 150m, EntryAtr = 1.2m, InitialSize = 2m, StopPrice = 148m,
                    TakeProfitPrice = 154m, HighWater = 151m, PartialTaken = true
                }
            }));

            var loaded = Assert.Single(new PositionStateStore(path).Load());
            Assert.Equal(opened, loaded.OpenedUtc);
            Assert.Equal("executions", loaded.OpenedSource);
            Assert.True(loaded.PartialTaken);
            Assert.Equal("SOLUSDT|L", loaded.Key);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unchanged_state_is_not_rewritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"positions-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PositionStateStore(path);
            var list = new[] { new SavedPosition { Market = "SOLUSDT", IsLong = true, EntryPrice = 1m } };
            Assert.True(store.Save(list));
            Assert.False(store.Save(list));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Record_of_an_earlier_trade_does_not_match_a_different_position()
    {
        var saved = new SavedPosition { Market = "SOLUSDT", IsLong = true, EntryPrice = 150m, InitialSize = 2m };

        Assert.True(saved.Matches(150.1m, 2m));
        Assert.False(saved.Matches(160m, 2m));   // a different entry price: another trade
        Assert.False(saved.Matches(150m, 5m));   // bigger than the record ever was
    }
}

public class EntryOutcomeTests
{
    [Fact]
    public void Filled_status_or_quantity_means_filled()
    {
        Assert.Equal(AutoTrader.EntryOutcome.Filled,
            AutoTrader.ClassifyEntry(new OrderResponse { Status = "Filled" }, false));
        Assert.Equal(AutoTrader.EntryOutcome.Filled,
            AutoTrader.ClassifyEntry(new OrderResponse { Status = "PartiallyFilledCanceled", DealStock = 0.5m }, false));
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Rejected")]
    [InlineData("Deactivated")]
    [InlineData("PartiallyFilledCanceled")]
    public void Terminal_unfilled_status_is_not_a_fill_even_if_a_position_exists_elsewhere(string status)
    {
        Assert.Equal(AutoTrader.EntryOutcome.NotFilled,
            AutoTrader.ClassifyEntry(new OrderResponse { Status = status }, positionOnMarket: true));
    }

    [Fact]
    public void Without_a_final_state_a_position_on_the_market_counts_as_filled()
    {
        Assert.Equal(AutoTrader.EntryOutcome.Filled, AutoTrader.ClassifyEntry(null, true));
        Assert.Equal(AutoTrader.EntryOutcome.Unknown, AutoTrader.ClassifyEntry(null, false));
    }

    [Fact]
    public void Bybit_order_not_exists_is_recognised_as_already_gone()
    {
        var ex = new ApiException("/v5/order/cancel", HttpStatusCode.BadRequest,
            "retCode=110001 order not exists or too late to cancel | {}");
        Assert.True(AutoTrader.IsOrderAlreadyGone(ex));
        Assert.False(AutoTrader.IsOrderAlreadyGone(
            new ApiException("/v5/order/cancel", HttpStatusCode.BadRequest, "retCode=10001 params error")));
    }
}

public class EntryBlockedTests
{
    [Fact]
    public void Whitebit_trading_restriction_blocks_entries()
    {
        var ex = new ApiException("/api/v4/order/collateral/limit", HttpStatusCode.UnprocessableEntity,
            "{\"code\":10008,\"errors\":{\"error\":[\"Margin trading is not available for your account due to trading restrictions.\"]}}");
        Assert.True(AutoTrader.IsEntryBlocked(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"errors\":{\"amount\":[\"Too small\"]}}")]
    [InlineData(HttpStatusCode.BadRequest, "retCode=10001 params error")]
    [InlineData(HttpStatusCode.InternalServerError, "trading restrictions")]
    [InlineData(HttpStatusCode.TooManyRequests, "Margin trading is not available")]
    public void Ordinary_rejections_do_not_block_the_venue(HttpStatusCode status, string body)
    {
        Assert.False(AutoTrader.IsEntryBlocked(new ApiException("/x", status, body)));
    }
}

public class ClosedPnlMatchingTests
{
    private static readonly DateTime Opened = new(2026, 10, 4, 12, 45, 6, DateTimeKind.Utc);
    private static readonly DateTime Closed = Opened.AddSeconds(4);
    private static long Sec(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    private static ClosedPnl Rec(string order, DateTime at, decimal net, decimal size = 1m, decimal fee = 0.03m) => new()
    {
        PositionId = SyntheticPositionId.For("SOLUSDT", true),
        Market = "SOLUSDT", OrderId = order, CloseDate = Sec(at), NetPnl = net, ClosedSize = size,
        TotalTradeFee = fee, GrossPnl = net + fee, AvgEntryPrice = 100m, AvgExitPrice = 100m, Side = "long"
    };

    [Fact]
    public void Previous_trades_record_is_not_booked_to_the_new_trade()
    {
        var previous = Rec("old", Opened.AddHours(-3), -0.05m);
        var booked = new HashSet<string>();

        // The new trade's record has not posted yet.
        var matched = AutoTrader.MatchClosedRecords(
            new[] { previous }, previous.PositionId, "SOLUSDT", Opened, Closed, booked);

        Assert.Empty(matched);
    }

    [Fact]
    public void Already_booked_record_is_never_booked_twice()
    {
        var rec = Rec("a", Closed, -0.0191m);
        var booked = new HashSet<string> { rec.RecordKey };

        Assert.Empty(AutoTrader.MatchClosedRecords(new[] { rec }, rec.PositionId, "SOLUSDT", Opened, Closed, booked));
    }

    [Fact]
    public void Record_inside_the_trades_window_is_matched()
    {
        var rec = Rec("a", Closed, -0.0191m);
        var other = Rec("old", Opened.AddHours(-3), -0.05m);

        var matched = AutoTrader.MatchClosedRecords(
            new[] { rec, other }, rec.PositionId, "SOLUSDT", Opened, Closed, new HashSet<string>());

        Assert.Equal("a", Assert.Single(matched).OrderId);
    }

    [Fact]
    public void Partial_take_and_final_close_merge_into_one_trade()
    {
        var partial = Rec("p", Opened.AddMinutes(20), 0.40m, size: 1m, fee: 0.02m);
        var final = Rec("f", Closed.AddMinutes(30), -0.10m, size: 1m, fee: 0.02m);

        var matched = AutoTrader.MatchClosedRecords(
            new[] { final, partial }, partial.PositionId, "SOLUSDT", Opened, Closed.AddMinutes(30), new HashSet<string>());
        var merged = ClosedPnl.Merge(matched);

        Assert.Equal(2, matched.Count);
        Assert.Equal(0.30m, merged.NetPnl);
        Assert.Equal(0.04m, merged.TotalTradeFee);
        Assert.Equal(2m, merged.ClosedSize);
        Assert.Equal(final.CloseDate, merged.CloseDate);
    }

    [Fact]
    public void Order_id_venues_never_fall_back_to_a_different_position_id()
    {
        var wrongSide = Rec("short", Closed, 0.2m);
        wrongSide.PositionId = SyntheticPositionId.For("SOLUSDT", false);

        Assert.Empty(AutoTrader.MatchClosedRecords(
            new[] { wrongSide }, SyntheticPositionId.For("SOLUSDT", true), "SOLUSDT", Opened, Closed, new HashSet<string>()));
    }

    [Fact]
    public void Venues_without_order_ids_still_fall_back_to_market_and_time()
    {
        var rec = Rec("", Closed, 0.2m);
        rec.PositionId = 999;

        var matched = AutoTrader.MatchClosedRecords(
            new[] { rec }, 123, "SOLUSDT", Opened, Closed, new HashSet<string>());

        Assert.Single(matched);
    }
}
