# TraderApp — multi-venue collateral / linear scalper

An auto-trading bot for **WhiteBIT collateral** markets (default) with optional
**Bybit USDT linear** runners in the same process. It scores a set of common
technical signals, sizes positions from a risk budget rather than a fixed
notional, and manages exits with a break-even move, an ATR trailing stop and a
time stop — with an exchange-side stop attached to every entry so a position is
never unprotected.

> This is trading software, not trading advice. It can lose money. Run it in
> `--signals-only` and then dry-run for long enough to believe the behaviour
> before you give it an API key with trade permission.

### Venues

| `Trading.ExchangeMode` | What runs |
|---|---|
| `WhiteBIT` (default) | Existing WhiteBIT behaviour — same `Api.*` / `WHITEBIT_*` keys and `Trading.Markets` |
| `Bybit` | Bybit linear only — `Bybit.*` / `BYBIT_*` keys and `Bybit.Markets` (`SOLUSDT` form) |
| `Both` | Two parallel `AutoTrader` loops, separate equity / session / journal files |

WhiteBIT stays the default: if you never set Bybit keys or change `ExchangeMode`,
nothing about the live WhiteBIT bot changes.

---

## Quick start

```bash
dotnet build
```

**1. Watch the signals — no API key needed, no private endpoint touched:**

```bash
dotnet run --project TraderApp -- --signals-only
```

**2. Dry run — full logic including sizing, still sends no orders:**

```bash
dotnet run --project TraderApp -- --dry-run
```

**3. Live (the default — real orders, no prompt):**

```bash
dotnet run --project TraderApp
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

**Bybit (optional):** set `Trading.ExchangeMode` to `Bybit` or `Both`, then:

```bash
setx BYBIT_API_KEY "your-key"
setx BYBIT_API_SECRET "your-secret"
```

or `TRADER_Bybit__Key` / `TRADER_Bybit__Secret`. Use Unified Trading Account
linear perps. Bybit session state lands in `data/session-state-bybit.json` so a
WhiteBIT halt file is never overwritten.

---

## How it decides

Nine components vote independently; a trade needs a weighted score above
`Signals.MinScore` **and** at least `MinConfirmations` components agreeing
**and** a fresh trigger (RSI / Bollinger / volume / book / MACD / stochastic /
structure totaling `MinTriggerWeight`) **and** a clear direction edge
(`MinDirectionEdge` — |agreeing − opposing| weight) **and** no veto. Regime,
EMA and VWAP are background state — they can stay aligned for hours and must
not open a trade on their own. Neutral votes are ignored in the score so they
cannot dilute a real setup.

| Component | Weight | What it reads |
|---|---|---|
| `regime` | 0.28 | EMA50 on the higher timeframe — price above/below it and its slope |
| `rsi` | 0.20 | Wilder RSI: pullback through 50 when trending, band exits when chopping |
| `ema` | ≤0.18 | EMA9 vs EMA21, weighted by how far apart they are |
| `bollinger` / `vwap` | 0.14 | Mean reversion in chop, VWAP side in a trend (whichever applies) |
| `macd` | 0.12 | Histogram crossing through zero (12/26/9) |
| `stoch` | 0.12 | Slow stochastic %K/%D lift from oversold or roll from overbought |
| `volume` | 0.10 | Bar volume vs its 20-bar average, with the bar's direction |
| `book` | 0.10 | Resting bid/ask imbalance over the top 10 levels |
| `structure` | 0.10 | Donchian-20 break in a trend; engulfing bar in chop |

**Vetoes** — absolute, they override any score:

- spread wider than `MaxSpreadBps` (8 bps)
- ATR% outside `[MinAtrPct, MaxAtrPct]` — nothing to scalp, or a volatility spike
- RSI at a blow-off extreme (≥78 or ≤22)
- direction disagreeing with a decided higher-timeframe regime
- thin book: less than `MinBookDepthUsd` (1000 USDT) resting on the thinner side of the top 10 levels
- crowded perp funding, or a fresh BTC 1h flip against the alt (stand-aside rules)
- not enough candle history, empty book side

**Time gates** (in the risk layer, not the signal): new entries can be limited
to a UTC window with `EntryHoursUtcStart` / `EntryHoursUtcEnd`. The shipped
config trades 24h (both 0); the earlier 06–22 window was dropped because the
book-depth and spread vetoes already catch the thin Asian-session setups the
window was meant to avoid. Saturday/Sunday size is scaled by
`WeekendRiskScale` (0.5). Open positions are managed at every hour.

ADX decides which playbook is live: above `AdxTrendThreshold` (22) the bot takes
pullbacks in the trend; below it, band reversion.

---

## How it keeps the risk bounded

**Hard risk defaults** are tuned for small, stable sessions — fewer trades,
smaller size, and early cutouts — not for maximum activity.

**Sizing is derived from the stop, not chosen.** Quantity is set so that being
stopped out costs exactly `RiskPerTradePct` (0.25%) of collateral equity, then
clamped by the per-position and total notional ceilings and the market's own
`minAmount` / `minTotal` / `stepSize`. When `ConvictionRiskFloor` is below 1
(default 0.75), size also scales with the signal score: a setup that only just
clears `MinScore` risks the floor fraction, a clean 1.00 keeps full risk.

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

Cooldowns and the trades-per-hour / trades-per-day budgets are consumed when an
entry **fills**, not when it is placed - an order that times out unfilled costs
nothing.

**Circuit breakers:**

| Rule | Default | Effect |
|---|---|---|
| Session drawdown | −2% from start equity | Flatten + halt |
| Peak drawdown | −2% from session peak | Flatten + halt (protects winners from give-back) |
| Daily profit lock | +3% session | Continue at `ProfitLockRiskScale` (×0.5) until UTC midnight; `0` halts instead |
| Consecutive losses | 10 | Hard halt (`HaltOnConsecutiveLosses`). Fee-sized losses (within `FeeNoiseUsd`) count too; a fee-sized win does not reset the streak |
| Max concurrent | 2 | A long and a short may coexist |
| Same-direction cap | 1 | No stacked longs/shorts across alts — they move together, so two longs is one bet with double fees |
| Total notional | 160 | Must be ≥ slots × per-position cap, or the last slot trades crumbs (a 10 USDT SUI position paid full fees for a third of the intended risk) |
| Per-market cooldown | 300s | No re-entry churn |
| Trade budget | 6/hour, 16/day UTC | Caps overtrading |
| Margin call | on | Flatten + halt |

Set `HaltOnConsecutiveLosses` to `false` if you prefer a cooldown
(`LossCooldownMinutes`) instead of ending the session. Set
`MaxDailyProfitPct` / `MaxPeakDrawdownPct` to `0` to disable those locks.

**Profit lock is a throttle, not a stop.** With `ProfitLockRiskScale: 0.5`, once
the day is +3% the bot keeps trading at half `RiskPerTradePct` and half the
notional caps for the rest of the UTC day, and the peak-drawdown rule (−2%
from the high) still ends the day if the gains are given back. Set the scale to
`0` to get the original "flatten and park until midnight" behaviour. The lock
state survives a restart via the session-state file and releases at UTC midnight.

**Partial take-profit.** At `PartialTakeAtR` (1.5R) the bot closes
`PartialTakeFraction` (50%) with a reduce-only market order and moves the stop
on the remainder to entry+fees. The rest runs to the 3.0×ATR target or the
1.3×ATR trail. Both halves must clear the market's `minAmount`/`minTotal` or
the partial is skipped for that trade. The time stop is 300 minutes — long
enough for a 3×ATR target to be reachable on 15m bars, which 120 was not.

**Halt persistence:** a hard halt is written to `data/session-state.json` and the
process **parks** instead of exiting, so Railway's `restartPolicyType: ALWAYS`
cannot wipe the halt by respawning with streak 0. After a redeploy, the new
process restores the halt and parks again.

Day-scoped locks (**daily profit**, session drawdown, peak drawdown) **auto-resume
at the next UTC midnight** — the park loop re-baselines session equity and
clears the halt. Consecutive-loss and margin-call halts stay until you set
`TRADER_Risk__ClearHaltOnBoot=true` for one restart (then remove it), or delete
the state file. Mount a volume on `/app/data` if you want the file to survive
image rebuilds.

`BreakEvenEnabled` (default off in live config) is honoured by both live and
backtest exits — they share `ExitRules`. Trail starts at `TrailStartAtR` (1.5R).

**Scratch rule.** A trade that never worked (best excursion below
`ScratchMaxFavourableR`) is closed after `ScratchUnprofitableMinutes` (150) —
but only while it is still near flat (`ScratchOnlyAboveR`, −0.35R). Signal
quality shows the entry's edge arrives at a ~16-bar horizon, so the old 45-minute
scratch was closing trades at 3 bars; and scratching at −0.74R saved 0.26R while
forfeiting the whole recovery. On the Aug–Sep 2026 replay the change is worth
about +10 USDT per 45 days and +3 points of win rate.

---

## Configuration

Everything lives in [`TraderApp/appsettings.json`](TraderApp/appsettings.json).
The settings worth knowing:

| Key | Default | Meaning |
|---|---|---|
| `Trading.DryRun` | `false` | `true` or `--dry-run` logs decisions and sends no orders |
| `Trading.Markets` | `SOL, AAVE, XRP, SUI` | Collateral pairs with enough 15m ATR to clear ~0.2% fees; BTC/ETH usually too quiet |
| `Trading.EntryInterval` | `15m` | Set by fee economics - see below |
| `Trading.RegimeInterval` | `4h` | Higher-timeframe filter |
| `Trading.Leverage` | `3` | Account-wide, set at startup |
| `Trading.StopLossAtrMult` | `1.4` | Working stop distance |
| `Trading.TakeProfitAtrMult` | `3.0` | Target; sized so 0.1%/0.1% fees leave net edge |
| `Trading.PostOnlyEntries` | `false` | `false` crosses the spread to fill now; `true` rests at the near touch and may not fill |
| `Risk.RiskPerTradePct` | `0.25` | Percent of equity lost if the stop is hit |
| `Risk.MaxDailyDrawdownPct` | `2` | Session-start equity cutout |
| `Risk.MaxPeakDrawdownPct` | `2` | Drawdown from session peak (0 = off) |
| `Risk.MaxDailyProfitPct` | `3` | Green-day lock threshold (0 = off). 1.5% was two winners at this R profile |
| `Risk.MaxNotionalPerPositionUsd` | `80` | Must be ≥ what 0.25% risk sizes to (~70 USDT on SOL at 215 equity) or it silently clips the risk |
| `Risk.ProfitLockRiskScale` | `0.5` | After the lock: keep trading at this risk scale; `0` halts for the day |
| `Risk.WeekendRiskScale` | `0.5` | Risk multiplier on Saturday/Sunday UTC |
| `Trading.EntryHoursUtcStart` / `End` | `0` / `0` | New entries only in this UTC window (equal = always, e.g. 6/22 for Europe+US sessions) |
| `Trading.PartialTakeAtR` / `PartialTakeFraction` | `1.5` / `0.5` | Bank half at 1.5R, rest to break-even (0 = off) |
| `Trading.MaxPositionMinutes` | `300` | Time stop; must give a 3×ATR target room on 15m bars |
| `Trading.TradeJournalPath` | `data/trades.jsonl` | One JSON line per closed trade with its entry context |
| `Trading.PositionStatePath` | `data/positions.json` | Per-position state (verified opening time, stops, partial-take flag) so a restart resumes the trade instead of resetting its age. Bybit gets a `-bybit` sibling file; put it on a volume to survive redeploys |
| `Signals.MinBookDepthUsd` | `1000` | Thin-side depth over the top 10 levels below this is a veto |
| `Risk.MaxConcurrentPositions` | `2` | With `MaxSameDirectionPositions: 1` — one long plus one short at most |
| `Risk.MaxTotalNotionalUsd` | `160` | Slots × per-position cap; smaller and the last slot is undersized |
| `Trading.ScratchUnprofitableMinutes` / `ScratchOnlyAboveR` | `150` / `-0.35` | Scratch a dead trade after 150m, only if still above −0.35R |
| `Trading.IdleStatusLogMinutes` | `5` | Equity status line cadence while nothing is open |
| `Signals.PullbackTriggerEnabled` | `false` | Trend-pullback trigger (tag of EMA21, close back on the trend side, RSI in band, volume ≥ `PullbackMinVolumeRatio`). Measured break-even at best — see below |
| `Risk.MaxTradesPerHour` / `MaxTradesPerDay` | `6` / `16` | Overtrading budget |
| `Risk.HaltOnConsecutiveLosses` | `true` | 10 losses → session halt (else cooldown) |
| `Trading.LoopIntervalMs` | `2000` | Decision loop period |
| `Trading.StatusLogEveryTicks` | `20` | Position status cadence (~40s); the equity line is throttled to `IdleStatusLogMinutes` when flat. Blocked setups log once per new 15m bar |
| `Signals.MinScore` | `0.58` | Raise for fewer, higher-conviction trades |
| `Signals.MinDirectionEdge` | `0.18` | Abstain when \|agree − oppose\| weight is thinner than this (0 = off) |
| `Risk.ConvictionRiskFloor` | `0.75` | Scale size with score; 1 = always full `RiskPerTradePct` |
| `Signals.RequireMacdAlign` | `false` | When true, MACD histogram must agree with direction |
| `Signals.StandAsideEnabled` | `true` | Veto crowded funding or a BTC 1h flip against the alt |
| `Signals.MaxFundingRate` | `0.0002` | Predicted perp funding (0.02%) that refuses the crowded side |

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

**Live market set.** The bot scans `SOL_USDT`, `AAVE_USDT`, `XRP_USDT`, and
`SUI_USDT` — the liquid names that clear fees with room to spare. `MaxConcurrentPositions`
is 2 with `MaxSameDirectionPositions` 1, so extra pairs mainly improve
setup selection, not stacked exposure. BTC/ETH stay out: their 15m ATR is usually
below `MinAtrPct`. DOT works on volatility but is left off by default as the
wildest of the measured set.

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
| `DailyDigest` | `true` | At UTC midnight: yesterday's trades, win rate, profit factor, fee share, exits by reason, best/worst |
| `WatchdogPingUrl` | empty | healthchecks.io ping URL — alerts Telegram if pings stop |

**Trade journal.** Every closed trade is appended to `data/trades.jsonl` with
the realised result from the exchange *and* what the signal looked like at
entry: score, agreeing components, ATR%, spread, 1h regime, perp funding, BTC
regime, hour and weekday, and the risk scale in force. That is what the daily
digest is built from, and it is the file to load in a notebook when deciding
which filter to tighten — grade trades by `entry.components`, `entry.hourUtc`
or `entry.atrPct`, not by gut. Mount a volume on `/app/data` on Railway or the
file is lost on redeploy.

**When the bot is down.** A Railway deploy SIGTERMs the old replica; that is
planned, so it does **not** text “is down” (you will already have `Started`
from the new one). A crash that still hits `catch` does text `TraderApp is
down`. An OOM or hard kill **cannot** send from the dead process — that is
what the watchdog is for.

1. Create a check at [healthchecks.io](https://healthchecks.io) (free), period 5 minutes, grace 5 minutes.
2. Integrations → Telegram, same chat as the bot.
3. Railway variable `HEALTHCHECK_PING_URL` = the ping URL (not the fail URL).

The bot GETs that URL every `WatchdogPingMinutes` (default 5). If Railway dies
without SIGTERM, healthchecks.io notices the missed ping and Telegram fires.

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

## Backtesting

```bash
dotnet run --project TraderApp -- --backtest --days 45
```

Replays the real `SignalEngine` and `ExitRules` over historical candles. The
stop/exit logic lives in one place (`Trading/ExitRules.cs`) and is used by both
the live trader and the replay, so the backtest cannot measure a different
strategy than the one that trades.

| Flag | Effect |
|---|---|
| `--days N` | Window length (default 30, max 365). History is cached on disk. |
| `--sweep` | 81 combinations of MinScore / MinConfirmations / stop / target |
| `--fee X` | Assume a round-trip fee of X% instead of the market's |

**What it cannot model**, and therefore where it flatters reality: there is no
historical order book, so the `book` component always abstains and the spread
veto uses a fixed 3 bps; stops and trails are evaluated at bar close rather than
every few seconds; fills are complete at the modelled price with no queue,
partial fills or funding. When a bar touches both stop and target, the stop is
assumed to have filled first. Use it to compare configurations, not to forecast
PnL.

### Measured result, 2026-08-01 to 2026-09-15

```
Trades 203 | win rate 46.8% | needed 45.0%
Net -11.49 USDT   (gross +4.75, fees 16.24)
Exits: stop hit 82%, target hit 12%, scratch 6%
```

The signals have a real but thin directional edge. Fees are 3.4x that edge.
A sweep of 81 parameter sets produced **0 profitable configurations**, and
`MinScore` had no effect at all across 0.55/0.62/0.68 - the other gates bind
first, so tuning it does nothing.

Fee sensitivity on the same trades:

| Round-trip fee | Net |
|---|---|
| 0.20% (current) | -11.49 |
| 0.14% | -4.03 |
| 0.10% | -1.66 |
| 0.08% | -0.29 |
| 0.06% | +1.49 |

Break-even sits near 0.077% round trip, about 0.039% per side. The binding
constraint on this strategy is the fee tier, not the signal set - and even
below it the edge is worth roughly +1.5 USDT per 45 days at 40 USDT notional.

---

## Signal quality

```bash
dotnet run --project TraderApp -- --backtest --signal-quality --days 45
```

Measures whether entries predict direction, separately from stops, targets and
fees. The backtest answers "did this make money", which conflates prediction
with execution — a bad exit rule can bury a good signal, which is exactly what
was happening here.

Result over Aug–Sep 2026 (64 signals):

| Horizon | Mean move | Hit rate | t |
|---|---|---|---|
| 1 bar | +0.07% | 45.3% | 0.45 |
| 4 bars | +0.26% | 51.6% | 1.01 |
| **8 bars** | **+0.48%** | **59.4%** | **1.78** |
| 16 bars | +0.70% | 53.1% | 1.69 |

The edge appears at 8–16 bars (2–4 hours), not immediately. A break-even stop
at entry+fees closed a third of trades near zero long before that. Turning it
off, with a 120-minute hold cap, moved gross PnL across three independent
45-day windows from −1.38 to +14.71 — positive in all three.

**t below 2 means the result is still consistent with chance.** The exit change
is well supported (3/3 windows); the underlying edge is not yet proven.

Per-component value at the 8-bar horizon: `macd` (t=2.20) and `ema` (t=2.02)
carry information; `regime` (1.78) and `volume` (1.33) do not; `bollinger`,
`rsi`, `stoch`, `structure` and `vwap` voted on fewer than a quarter of signals,
so they mostly add parameters rather than information. `book` cannot be
evaluated here — the replay has no historical order book.

---

## Project layout

```
Dockerfile                     Railway worker image
railway.json                   Railway build/deploy (no HTTP healthcheck)
NuGet.config                   pin restore to nuget.org
Directory.Build.props          stamp version from BUILD_NUMBER / git SHA
TraderApp/
  Program.cs                   entry point, modes, banner
  BuildInfo.cs                 assembly version shown in the banner
  appsettings.json             all tunables
  Configuration/BotConfig.cs   typed config + validation
  Exchange/
    WhiteBitClient.cs          signed REST, nonces, pacing, backoff
    WhiteBitModels.cs          request/response DTOs
    MarketRules.cs             tickSize/stepSize quantisation, minimums
  Indicators/
    Candle.cs                  kline parsing
    Ta.cs                      EMA, SMA, RSI, ATR, ADX, Bollinger, VWAP
  Signals/
    SignalEngine.cs            confluence scoring + vetoes
    StandAside.cs              funding / BTC-flip stand-aside rules
  Risk/
    RiskManager.cs             sizing, limits, circuit breakers, risk scaling
    SessionStateStore.cs       halt / lock persistence across restarts
  Notifications/
    TelegramNotifier.cs        queued, non-blocking Telegram delivery
  Backtest/
    BacktestRunner.cs          --backtest entry point, reporting, sweep
    SignalQuality.cs           --signal-quality: does the entry predict?
    Backtester.cs              bar-by-bar replay
    HistoryLoader.cs           paged kline download with disk cache
  Trading/
    AutoTrader.cs              the loop, entries, position management, partial takes
    ExitRules.cs               stop/exit logic shared by live and backtest
    TradeJournal.cs            JSONL trade log + daily digest
    SignalMonitor.cs           --signals-only mode
    Log.cs                     console + file logging + notifier hook
```

Logs are written to `bin/<config>/net10.0/logs/trader-YYYYMMDD.log`.

---

## Operational notes

- **Collateral must be quote-only.** The drawdown and profit locks read
  collateral *equity*. If the collateral account holds BTC/SOL/anything but
  USDT, their price moves count as PnL: a dip can halt the bot with no trade
  taken and a rally can trip the profit lock. At boot the bot lists any
  non-quote collateral (`⚠️ collateral holds non-USDT assets…`) and at runtime
  warns when equity moves ≥0.25% while nothing is open. If you see either,
  convert the assets or move them to the main account.
- **The trend-pullback trigger is off by default, deliberately.** It exists
  because a strong trend (AAVE at RSI 81–88 for 90 minutes) is otherwise
  unreachable — "late long" blocks the chase and nothing fires on the pullback.
  Measured on the Aug–Sep 2026 replay it added ~100 trades per 45 days that were
  net negative at `PullbackMinVolumeRatio` 1.0 and break-even at 1.3. Enable it
  only if the journal shows trend days being missed.
- **Restart safety.** Positions already open are adopted on startup and managed
  with stops derived from current ATR. Ctrl+C cancels resting entry orders but
  deliberately does **not** close positions — they keep their exchange-side
  stop, and force-closing on restart would turn routine maintenance into a
  realised loss.
- **When the exchange refuses to close a position.** WhiteBIT can disable margin
  trading on an account (`HTTP 422, code 10008: Margin trading is not available
  for your account due to trading restrictions`). The bot treats that as
  permanent: it sends one `🚫 … is refusing to close positions` alert, pauses
  new entries on that venue (a bot that cannot exit must not enter), keeps
  tracking the stuck position in the status log, retries the close every
  5 minutes and reminds you hourly. Close the position in the exchange UI and
  sort out the restriction with WhiteBIT; when a retry eventually succeeds you
  get a `✅` and entries stay paused until you send `/resume`. Any other close
  failure is retried with a 5 s → 5 min backoff, with only the first and every
  fifth failure reaching Telegram.
- **Hedge mode.** The client passes through `positionSide`; if you enable hedge
  mode on the account, set it on the order payloads.
- **Rate limits.** Private requests are serialised with a configurable minimum
  gap and retried with exponential backoff and jitter on 429/5xx. Bybit reports
  throttling as HTTP 200 with `retCode 10006`; that is retried too, honouring
  `X-Bapi-Limit-Reset-Timestamp` when present. Closed-bar klines are cached per
  market/interval until the next bar closes, so steady-state traffic is a
  handful of calls per tick, not a history download per market.

---

## Controlling the bot from Telegram

Send the bot a command; it replies in the same chat.

| Command | What it does |
|---|---|
| `/status` | Mode, version, balance, open vs max positions, last tick age |
| `/positions` | Each open position with its stop, target, best excursion and unrealised PnL |
| `/pause` | Stop taking new setups. Open positions stay fully managed |
| `/resume` | Take new setups again |
| `/version` | Build number, commit and build time |
| `/restart` | Graceful restart |
| `/help` | Lists the registered commands |

### How restart works

`/restart` does not call the Railway API. It triggers the same graceful
shutdown as SIGTERM - the loop finishes its cycle, resting entry orders are
cancelled, queued Telegram messages flush - and then the process **exits with
code 75**.

The non-zero code is load-bearing. Railway's `ALWAYS` restart policy is **not
available on Free or Trial plans**: those silently fall back to the default
`ON_FAILURE`, which restarts only on a non-zero exit. A clean exit 0 leaves the
deployment "Completed" and stopped - which is exactly what happened the first
time this was tried. Exiting 75 restarts under `ON_FAILURE` and `ALWAYS` alike,
and systemd's `Restart=always` covers it on a VPS.

A genuine stop still exits 0: SIGTERM from a deploy, or Ctrl+C, is the platform
asking the process to go away, not a request to come back.

Note that `ON_FAILURE` is capped at **10 restarts on Free/Trial**, so `/restart`
is not something to use in a loop. After that Railway leaves the service down
until you redeploy or restart it from the dashboard.

That avoids storing a Railway API token in the container, which would carry
rights over the whole project just to restart one service. Run locally with no
supervisor and `/restart` simply stops the bot.

Open positions are not closed. They keep the exchange-side OTO stop attached at
entry and are re-adopted on boot.

### What pause does, and does not, do

`/pause` blocks **new entries only**. Everything already open keeps being
managed - stops, targets, trailing, partial take and the time stop all still
fire. It is the safe way to step back from a market without abandoning what you
are holding.

It deliberately does **not** flip `DryRun`. That would stop the bot sending any
order at all, including exits, leaving an open position with nothing under it
but the wider exchange-side stop. If you want that, set
`TRADER_Trading__DryRun=true` in Railway variables and redeploy - but not while
holding a position.

The pause is written to `data/session-state.json`, so a redeploy or crash cannot
silently resume trading on a bot you paused on purpose. Unlike the day-scoped
profit and drawdown locks it never clears itself; only `/resume` does.

### Security

Only messages from the configured `Telegram.ChatId` are acted on. A Telegram
bot is reachable by anyone who finds its handle, so without that check a
stranger could restart a bot trading real money; commands from any other chat
are dropped and logged once.

Telegram long polling is exclusive per token: if two instances have
`CommandsEnabled` at once, Telegram answers 409 and the second logs a warning
and stops listening. Keep it enabled on one instance only - if you run a local
copy alongside Railway, set `TRADER_Telegram__CommandsEnabled=false` locally.

Set `Telegram.CommandsEnabled: false` to turn the listener off entirely.

Commands queued while the bot is down are **discarded on startup** rather than
replayed. Without that, a `/restart` sent while it was offline would fire on
boot and restart it again, forever.

---

## Deploying to Railway (no VPS)

Railway runs the bot as a container and redeploys on every push to `main`.
`Dockerfile` and `railway.json` in this repo are all it needs.

### 1. Push to a private GitHub repo

```bash
git remote add origin https://github.com/YOU/TraderApp.git
```

```bash
git push -u origin main
```

### 2. Create the project

[railway.com](https://railway.com) → **New Project** → **Deploy from GitHub
repo** → pick the repo. Railway finds the root `Dockerfile` and builds with it;
the first build takes a few minutes because it pulls the .NET SDK image.

### 3. Set variables

Service → **Variables** → add:

| Name | Value |
|---|---|
| `WHITEBIT_API_KEY` | your key |
| `WHITEBIT_API_SECRET` | your secret |
| `TELEGRAM_BOT_TOKEN` | bot token |
| `TELEGRAM_CHAT_ID` | `289040655` |
| `TRADER_Trading__DryRun` | `true` |
| `HEALTHCHECK_PING_URL` | healthchecks.io ping URL (optional) |

The double underscore in `TRADER_Trading__DryRun` is the config binder's
separator for nested keys; it maps to `Trading.DryRun` and beats
`appsettings.json`. Leave it `true` until you have watched it run.

### 4. Do not add a domain

This is a worker with no HTTP listener. Railway only exposes a service if you
generate a domain, so simply skip that — and do not set `healthcheckPath` in
`railway.json`, because a healthcheck on a service with no port fails every
deploy.

### 5. Verify

Watch the **Deploy Logs**. The startup banner should read `MODE : DRY RUN`, and
the Telegram `Started (DRY RUN)` message should arrive. That message is the real
confirmation — it proves credentials, network and notifications all work.

### 6. Go live when you trust it

Set `TRADER_Trading__DryRun` to `false` and redeploy.

### Things worth knowing

**Rollback** is Deployments → pick the previous one → **Redeploy**. Faster than
a revert commit, which matters when the thing you are rolling back trades money.

**`watchPatterns`** in `railway.json` limits redeploys to changes under
`TraderApp/`, the `Dockerfile`, `NuGet.config` and `railway.json`. A redeploy
restarts the bot, and while it is down only the exchange-side stop protects an
open position — no reason to take that window for a README edit.

**The filesystem is ephemeral.** The bot's own `logs/` directory is wiped on
every redeploy. Railway's log viewer and Telegram are the durable record, which
is a good reason to keep `HeartbeatMinutes` non-zero.

**Static outbound IP needs the Pro plan** ($20/mo, three IPs shared with other
customers). On Hobby the egress IP is not stable, so you cannot IP-allowlist the
WhiteBIT API key — the key is then protected only by being secret and
trade-only. A VPS is the cheaper way to get a dedicated fixed IP.

**Auto-deploy has no human gate.** Every push to `main` changes what is trading
your money. If that makes you uneasy, disable auto-deploy in service settings
and press Deploy manually, or work on branches and merge deliberately.

**`NuGet.config` pins restore to nuget.org.** Without it a private feed on the
build machine can 401 and fail the Railway image build.

---
