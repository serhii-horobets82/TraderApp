using Newtonsoft.Json;
using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace TraderApp // Note: actual namespace depends on the project name.
{

    interface IPayload
    {
        public bool NonceWindow { get; set; }
        public string Request { get; set; }
        public string Nonce { get; set; }
    }

    class Payload : IPayload
    {
        [JsonProperty("request")]
        public string Request { get; set; }

        [JsonProperty("nonce")]
        public string Nonce { get; set; }

        [JsonProperty("nonceWindow")]
        public bool NonceWindow { get; set; }

        [JsonProperty("ticker")]
        public string Ticker { get; set; }
    }

    class LimitOrder : Payload
    {
        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("price")]
        public string Price { get; set; }
    }


    /*
    {
    "positionId": 527,                         // position ID
    "market": "BTC_USDT",                      // market name
    "openDate": 1651568067.789679,             // date of position opening
    "modifyDate": 1651568067.789679,           // date of position modifying (this is date of current event)
    "amount": "0.1",                           // amount of order
    "basePrice": "45658.349",                  // base price of position
    "liquidationPrice": null,                  // liquidation price according to current state of position
    "liquidationState": null,                  // state of liquidation. Possible values: null, margin_call, liquidation
    "pnl": "-168.42",                          // current profit and loss in **money**
    "pnlPercent": "-0.43",                     // current profit and loss in percentage
    "margin": "8316.74",                       // amount of funds in open position **money**
    "freeMargin": "619385.67",                 // free funds for trading according to
    "funding": "0",                            // funding that will be paid on next position stage change (order, liquidation, etc)
    "unrealizedFunding": "0.0019142920201966", // funding that will be paid on next position stage change (order, liquidation, etc)
    }
   */
    class PositionBase
    {
        [JsonProperty("positionId")]
        public long PositionId { get; set; }

        [JsonProperty("market")]
        public string Market { get; set; }

        [JsonProperty("openDate")]
        public string OpenDate { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("basePrice")]
        public string BasePrice { get; set; }

        [JsonProperty("liquidationPrice")]
        public string LiquidationPrice { get; set; }

        [JsonProperty("liquidationState")]
        public string LiquidationState { get; set; }
    }

    class Position : PositionBase
    {
        [JsonProperty("pnl")]
        public decimal Pnl { get; set; }

        [JsonProperty("pnlPercent")]
        public string PnlPercent { get; set; }

        [JsonProperty("margin")]
        public string Margin { get; set; }

        [JsonProperty("freeMargin")]
        public string freeMargin { get; set; }

        [JsonProperty("funding")]
        public string Funding { get; set; }

        [JsonProperty("unrealizedFunding")]
        public string UnrealizedFunding { get; set; }
    }

    public class Order
    {
        [JsonProperty("orderId")]
        public string OrderId { get; set; }
    }

    internal class Program
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private static string apiKey = "811d1819b6786ac56c7b85bfd32b3d35"; // put here your public key
        private static string apiSecret = "d86a88f708f571c6d560bdb2697c8556"; // put here your secret key
        private static string hostname = "https://whitebit.com"; // domain without last slash. Do not use whitebit.com/

        private static Timer? _timer;

        private async static void TimerCallback(Object o)
        {
            var response = await CallApi<Position[]>("/api/v4/collateral-account/positions/open", new LimitOrder { Market = "SOL_USDT" });
            if (response != null && response.Length > 0)
            {
                foreach (var item in response)
                {
                    Console.WriteLine($"PNL: {item.Pnl}, {item.Market}");
                    if (item.Pnl > 20)
                    {
                        Console.WriteLine("Order {0}", JsonConvert.SerializeObject(item));
                        var x = await CallApi<Order>("/api/v4/order/collateral/limit",
                            new LimitOrder
                            {
                                Market = item.Market,
                                Amount = Math.Abs(item.Amount),
                                Side = item.Amount < 0 ? "buy" : "sell",
                                Price = "26"
                            });
                        Console.WriteLine(x);
                    }
                }
            }
            else
                Console.WriteLine("Skip");
        }
        private static async Task Main()
        {
            _timer = new Timer(TimerCallback, null, 0, 1000);
            Console.ReadLine();
            _timer.Dispose();

            //var responseBody = await CallApi("/api/v4/trade-account/balance");
            //Console.WriteLine(responseBody);
            //Console.ReadKey();

            //responseBody = await CallApi("/api/v4/orders");
            //Console.WriteLine(responseBody);
            //Console.ReadKey();

            //responseBody = await CallApi("/api/v4/collateral-account/positions/open");
            //Console.WriteLine(responseBody);

            //Console.ReadKey();
        }

        private static async Task<T?> CallApi<T>(string method, IPayload? payloadData = null)
        {
            var data = payloadData ?? new Payload();

            data.Request = method;
            data.NonceWindow = false;
            data.Nonce = GetNonce();

            var dataJsonStr = JsonConvert.SerializeObject(data);
            var payload = Base64Encode(dataJsonStr);
            var signature = CalcSignature(payload, apiSecret);

            var content = new StringContent(dataJsonStr, Encoding.UTF8, "application/json");
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{hostname}{method}")
            {
                Content = content
            };
            requestMessage.Headers.Add("X-TXC-APIKEY", apiKey);
            requestMessage.Headers.Add("X-TXC-PAYLOAD", payload);
            requestMessage.Headers.Add("X-TXC-SIGNATURE", signature);

            var response = await _httpClient.SendAsync(requestMessage);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var err = responseBody;
            }
            return default;
        }

        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        public static string CalcSignature(string text, string apiSecret)
        {
            using (var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(apiSecret)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLower();
            }
        }
        private static string GetNonce()
        {
            var milliseconds = (long)DateTime.Now.ToUniversalTime()
                .Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalMilliseconds;

            return milliseconds.ToString();
        }
    }
}