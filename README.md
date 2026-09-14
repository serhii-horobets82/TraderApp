# TraderApp — WhiteBIT collateral scalper

An auto-trading bot for WhiteBIT collateral (margin) markets. It scores a set of
common technical signals, sizes positions from a risk budget rather than a fixed
notional, and manages exits with a break-even move, an ATR trailing stop and a
time stop — with an exchange-side stop attached to every entry so a position is
never unprotected.

> This is trading software, not trading advice. It can lose money. Run it in
> `--signals-only` and then dry-run for long enough to believe the behaviour
> before you give it an API key with trade permission.

---

## Quick start

```bash
dotnet build
```

**1. Watch the signals — no API key needed, no private endpoint touched:**

```bash
dotnet run --project TraderApp -- --signals-only
```

**2. Dry run — full logic including sizing, still sends no orders (the default):**

```bash
dotnet run --project TraderApp
```

**3. Live — requires both config flags *and* typing `LIVE` at the prompt:**

```bash
dotnet run --project TraderApp -- --live
```

### Credentials

Never in `appsettings.json`. Use environment variables:

```bash
setx WHITEBIT_API_KEY "your-public-key"
```

```bash
setx WHITEBIT_API_SECRET "your-secret-key"
```

`TRADER_Api__Key` / `TRADER_Api__Secret` work too, as does a git-ignored
`appsettings.local.json`. Give the key **trade** permission only — never
withdrawal — and IP-allowlist it in the WhiteBIT UI.

---

## How it decides

Six components vote independently; a trade needs a weighted score above
`Signals.MinScore` **and** at least `MinConfirmations` components agreeing
**and** a fresh trigger (RSI / Bollinger / volume / book totaling
`MinTriggerWeight`) **and** no veto. Regime, EMA and VWAP are background
state — they can stay aligned for hours and must not open a trade on their
own. Neutral votes are ignored in the score so they cannot dilute a real
setup.

| Component | Weight | What it reads |
|---|---|---|
| `regime` | 0.28 | EMA50 on the higher timeframe — price above/below it and its slope |
| `rsi` | 0.20 | Wilder RSI: pullback through 50 when trending, band exits when chopping |
| `bollinger` / `vwap` | 0.14 | Mean reversion in chop, VWAP side in a trend (whichever applies) |
| `ema` | ≤0.18 | EMA9 vs EMA21, weighted by how far apart they are |
| `volume` | 0.10 | Bar volume vs its 20-bar average, with the bar's direction |
| `book` | 0.10 | Resting bid/ask imbalance over the top 10 levels |

**Vetoes** — absolute, they override any score:

- spread wider than `MaxSpreadBps` (8 bps)
- ATR% outside `[MinAtrPct, MaxAtrPct]` — nothing to scalp, or a volatility spike
- RSI at a blow-off extreme (≥78 or ≤22)
- direction disagreeing with a decided higher-timeframe regime
- not enough candle history, empty book side

ADX decides which playbook is live: above `AdxTrendThreshold` (22) the bot takes
pullbacks in the trend; below it, band reversion.

---

## How it keeps the risk bounded

**Sizing is derived from the stop, not chosen.** Quantity is set so that being
stopped out costs exactly `RiskPerTradePct` (0.25%) of collateral equity, then
clamped by the per-position and total notional ceilings and the market's own
`minAmount` / `minTotal` / `stepSize`.

**Two stops, on purpose:**

- the **bot stop** at 1.4×ATR is the working stop — it moves to break-even+fees
  at 1.3R and then trails at 1.3×ATR;
- the **hard stop** at 1.8× that distance is attached to the entry order itself
  as an OTO `stopLoss`, together with the `takeProfit`. If this process is
  killed, loses its network, or crashes, the exchange still closes the position.

**Fee-aware targets.** WhiteBIT quotes 0.1% maker / 0.1% taker on the majors, so
a round trip costs ~0.2% of notional. Any setup whose target does not clear
round-trip fees plus `MinNetEdgePct` is rejected before sizing. This is why a
naive "close at +0.4% PnL" scalp bleeds: much of that 0.4% is the fee.

**Entry execution.** WhiteBIT charges 0.1% maker and 0.1% taker on these
markets, so resting a post-only order earns no fee discount - it only avoids
paying the spread, while risking never filling at all. The default therefore
quotes the far touch (`PostOnlyEntries: false`) so entries fill immediately.
It is still a limit order, so slippage is capped at the spread, and the 8 bps
`MaxSpreadBps` veto caps that at roughly 0.08% against targets of ~0.30-2.0%.
Set `PostOnlyEntries: true` to go back to passive resting.

Cooldowns and the trades-per-hour budget are consumed when an entry **fills**,
not when it is placed - an order that times out unfilled costs nothing.

**Circuit breakers:** session drawdown cutout (−3% equity → flatten and halt),
margin-call flatten, max concurrent positions, max total notional, per-market
cooldown, max trades/hour, and a consecutive-loss cooldown.

---

## Configuration

Everything lives in [`TraderApp/appsettings.json`](TraderApp/appsettings.json).
The settings worth knowing:

| Key | Default | Meaning |
|---|---|---|
| `Trading.DryRun` | `true` | Must be `false` **and** `IUnderstandThisTradesRealMoney: true` to trade |
| `Trading.EntryInterval` | `15m` | Set by fee economics - see below |
| `Trading.RegimeInterval` | `4h` | Higher-timeframe filter |
| `Trading.Leverage` | `3` | Account-wide, set at startup |
| `Trading.StopLossAtrMult` | `1.4` | Working stop distance |
| `Trading.TakeProfitAtrMult` | `3.0` | Target; sized so 0.1%/0.1% fees leave net edge |
| `Trading.PostOnlyEntries` | `false` | `false` crosses the spread to fill now; `true` rests at the near touch and may not fill |
| `Risk.RiskPerTradePct` | `0.25` | Percent of equity lost if the stop is hit |
| `Risk.MaxDailyDrawdownPct` | `3` | Session cutout |
| `Risk.MaxConcurrentPositions` | `2` | |
| `Signals.MinScore` | `0.62` | Raise for fewer, higher-conviction trades |

### Why the entry timeframe is 15m

Two measurements drove this, both taken against the live API on 2026-09-13.

**1m bars are too sparse to trade.** Across 120 bars per market, 7% of BTC_USDT
1m bars and 16% of WBT_USDT 1m bars had literally zero trades, which makes
volume confirmation meaningless and collapses ATR below the fee threshold.

**5m bars are clean but too small to pay for themselves.** At 0.1% maker /
0.1% taker a round trip costs 0.2% of notional, so a target must exceed ~0.25%
to leave any net edge. Measured ATR%:

| Market | ATR% 5m | ATR% 15m | Target @3.0x on 5m | on 15m |
|---|---|---|---|---|
| ETH_USDT | 0.077 | 0.179 | 0.231% ✗ | 0.537% ✓ |
| SOL_USDT | 0.126 | 0.256 | 0.378% ✓ | 0.768% ✓ |
| XRP_USDT | 0.140 | 0.250 | 0.420% ✓ | 0.750% ✓ |
| AAVE_USDT | 0.141 | 0.330 | 0.423% ✓ | 0.990% ✓ |
| AVAX_USDT | 0.149 | 0.279 | 0.447% ✓ | 0.837% ✓ |
| SUI_USDT | 0.173 | 0.335 | 0.519% ✓ | 1.005% ✓ |
| DOT_USDT | 0.420 | 0.651 | 1.260% ✓ | 1.953% ✓ |
| BTC_USDT | 0.038 | 0.101 | 0.114% ✗ | 0.303% ✓ |

On 5m several majors still fail or barely clear the 0.30% fee+edge floor; on 15m all eight can. The 3.0× target vs 1.4× stop is what leaves a usable payoff after a 0.2% round trip — 1.6× ATR did not.

**The honest framing:** at 0.1%/0.1%, true scalping is not viable on WhiteBIT.
A 0.2% round trip against a 0.13% average 5m move loses regardless of signal
quality. What this bot actually runs is short-horizon intraday momentum with
~0.30-2.0% targets and holds of roughly one to three hours. If your fee tier
drops to 0.05%/0.05% the round trip halves to 0.1%, six of eight markets clear
on 5m, and genuine scalping becomes possible - at which point set
`EntryInterval` back to `5m` and `MaxPositionMinutes` back to `90`.

The bot reads your account's real fee at startup via
`POST /api/v4/market/fee/single` rather than assuming the public default, and
logs the resulting round-trip cost. Watch that line: it sets the minimum move
every trade must capture.

---

## Telegram notifications

Sends the events you would want to see from a phone while the bot runs
unattended: startup summary, entries and fills, stop moves, exits, circuit
breakers and errors, plus a periodic status heartbeat.

**Setup.** Message [@BotFather](https://t.me/BotFather) on Telegram, create a
bot, and copy the token it gives you. Then:

```bash
set TELEGRAM_BOT_TOKEN=123456:your-token-here
```

Send any message to your new bot, then run:

```bash
dotnet run --project TraderApp -- --telegram-test
```

It verifies the token, prints the chat ids that have messaged the bot, and
sends a test message once `ChatId` is set. Put the id in `TELEGRAM_CHAT_ID` or
in `appsettings.json` as `Telegram.ChatId`, and set `Telegram.Enabled: true`.

**What gets sent** is controlled per category:

| Setting | Default | Covers |
|---|---|---|
| `SendTrades` | `true` | Position opened; position closed with P/L and the new balance |
| `SendRisk` | `true` | Halts, drawdown cutout, margin call, forced flattening |
| `SendErrors` | `true` | API and loop failures |
| `Verbose` | `false` | Every raw log line - signal breakdowns, order ids, stop moves |
| `HeartbeatMinutes` | `60` | Periodic balance and open positions; `0` disables |

Messages are deliberately short - the result, not the reasoning:

```
WIN ETH long
P/L +0.83 USDT  (+0.33%)
Balance 217.21 USDT
```

```
Balance 216.38 USDT  (-1.47 session)
ETH long +0.12 USDT
SOL short -0.04 USDT
```

The realised P/L comes from `/api/v4/collateral-account/positions/closed-pnl`,
so it is net of trading fees and funding - not an estimate from the last
unrealised figure, and not inferred from an equity delta, which would be wrong
whenever more than one position is open. Set `Verbose: true` to get the full
firehose back for debugging.

The heartbeat exists because silence from a trading bot is ambiguous - it could
be running with no signals, or dead. The heartbeat tells the two apart.

**Telegram is never on the trading path.** Messages are queued and sent from a
background task, bursts are batched into a single send, the queue is bounded and
drops oldest under flood, and send failures are logged locally and discarded.
Measured: 50 log calls with a dead Telegram endpoint complete in 5 ms and no
exception reaches the caller.

**Two things to be aware of.** The bot token is a credential - keep it in an
environment variable, not in `appsettings.json`. And enabling this sends your
trading activity (markets, sizes, prices, PnL) to Telegram's servers.

---

## Project layout

```
TraderApp/
  Program.cs                   entry point, modes, banner, confirmation
  appsettings.json             all tunables
  Configuration/BotConfig.cs   typed config + validation
  Exchange/
    WhiteBitClient.cs          signed REST, nonces, pacing, backoff
    WhiteBitModels.cs          request/response DTOs
    MarketRules.cs             tickSize/stepSize quantisation, minimums
  Indicators/
    Candle.cs                  kline parsing
    Ta.cs                      EMA, SMA, RSI, ATR, ADX, Bollinger, VWAP
  Signals/SignalEngine.cs      confluence scoring + vetoes
  Risk/RiskManager.cs          sizing, limits, circuit breakers
  Notifications/
    TelegramNotifier.cs        queued, non-blocking Telegram delivery
  Trading/
    AutoTrader.cs              the loop, entries, position management
    SignalMonitor.cs           --signals-only mode
    Log.cs                     console + file logging + notifier hook
```

Logs are written to `bin/<config>/net10.0/logs/trader-YYYYMMDD.log`.

---

## Operational notes

- **Restart safety.** Positions already open are adopted on startup and managed
  with stops derived from current ATR. Ctrl+C cancels resting entry orders but
  deliberately does **not** close positions — they keep their exchange-side
  stop, and force-closing on restart would turn routine maintenance into a
  realised loss.
- **Hedge mode.** The client passes through `positionSide`; if you enable hedge
  mode on the account, set it on the order payloads.
- **Rate limits.** Private requests are serialised with a configurable minimum
  gap and retried with exponential backoff and jitter on 429/5xx.
