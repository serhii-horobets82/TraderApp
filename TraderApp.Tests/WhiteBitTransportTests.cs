using System.Net;
using System.Text;
using TraderApp.Configuration;
using TraderApp.Exchange;

namespace TraderApp.Tests;

public class WhiteBitTransportTests
{
    private sealed class DropsFirstResponseHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) throw new HttpRequestException("The response ended prematurely.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"equity\":42.5}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task Private_account_read_retries_a_dropped_response()
    {
        var handler = new DropsFirstResponseHandler();
        using var client = new WhiteBitClient(new ApiConfig
        {
            BaseUrl = "https://whitebit.example",
            Key = "key",
            Secret = "secret",
            MaxRetries = 1
        }, handler);

        var summary = await client.GetAccountSummaryAsync(CancellationToken.None);

        Assert.Equal(42.5m, summary!.Equity);
        Assert.Equal(2, handler.Calls);
    }
}
