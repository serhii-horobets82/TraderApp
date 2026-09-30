using System.Net;
using TraderApp.Exchange;
using TraderApp.Risk;
using TraderApp.Configuration;
using TraderApp.Trading;

namespace TraderApp.Tests;

/// <summary>
/// A rejected close must be classified correctly: an account restriction is
/// permanent (pause entries, retry slowly), anything else is transient
/// (back off, keep trying). Getting this wrong either floods Telegram every
/// two seconds or leaves the bot opening positions it cannot exit.
/// </summary>
public class CloseFailureTests
{
    // Verbatim WhiteBIT response from 2026-10-01 when margin was disabled on the account.
    private const string RestrictedBody =
        "{\"code\":10008,\"errors\":{\"error\":[\"Margin trading is not available for your account due to trading restrictions.\"]},\"message\":\"Validation failed\"}";

    [Fact]
    public void Whitebit_trading_restriction_is_a_blocked_exit()
    {
        var ex = new ApiException("/api/v4/collateral-account/position/close", HttpStatusCode.UnprocessableEntity, RestrictedBody);
        Assert.True(AutoTrader.IsExitBlocked(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"code\":1,\"errors\":{\"positionId\":[\"Position not found\"]}}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":0,\"message\":\"Bad request\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "Margin trading is not available")] // 5xx is never treated as a refusal
    [InlineData(HttpStatusCode.TooManyRequests, "trading restrictions")]
    public void Other_failures_are_transient(HttpStatusCode status, string body)
    {
        var ex = new ApiException("/api/v4/collateral-account/position/close", status, body);
        Assert.False(AutoTrader.IsExitBlocked(ex));
    }

    [Fact]
    public void Pause_reason_is_logged_and_pause_is_idempotent()
    {
        var risk = new RiskManager(new RiskConfig { PersistSessionState = false }, new TradingConfig());
        Assert.True(risk.Pause("- the venue is rejecting closes"));
        Assert.True(risk.EntriesPaused);
        Assert.False(risk.Pause()); // already paused: a second caller must not reset PausedAtUtc
        Assert.True(risk.Resume());
        Assert.False(risk.EntriesPaused);
    }
}
