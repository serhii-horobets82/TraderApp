# TraderApp operating guide

This guide describes the bot as it runs today. It is an implementation guide, not investment advice or a promise of profit.

## Purpose and venues

TraderApp trades short-horizon momentum setups on WhiteBIT collateral markets, Bybit USDT-linear perpetuals, or both independently. Each venue has separate risk state, journals and position-state files, so a venue problem cannot stop exits on the other one.

The current live configuration covers SOL, AAVE, XRP and SUI against USDT. It evaluates new entries on a closed 15-minute candle with a one-hour regime filter. This is not a one-minute scalper: normal holds range from minutes to hours.

## Startup sequence

At boot the bot loads market tick/size/minimum rules, reads account-specific fees, reads equity and leverage, restores persisted risk state, and adopts any exchange position already open. Redeploys deliberately do not force-close positions; the exchange-side protective stop remains active during the handover.

It then reads actual maker/taker fees per configured market. Those fees—not public defaults—define the minimum move a trade must capture.

## Quote-adjusted risk equity

WhiteBIT collateral may contain ETH, BTC and other assets as well as USDT. Total account equity then moves as those assets move even if the strategy has no trade. This could trigger a false drawdown halt or profit lock.

With `Risk.ExcludeNonQuoteCollateralFromRiskEquity=true`, the default, the bot marks non-USDT collateral with public order-book mid prices every `Risk.CollateralValuationRefreshSeconds`. It subtracts that value from account equity for bot sizing, drawdown, peak-drawdown and profit-lock calculations. Startup logs show account equity, excluded collateral value and risk equity.

This does not sell or transfer those assets, and does not eliminate WhiteBIT cross-margin liquidation risk. It only prevents ETH/BTC wallet movements being booked as bot PnL. Keep enough free USDT and watch the exchange margin ratio.

## Signal and entry rules

The bot only considers a fresh closed entry candle. It combines EMA trend, higher-timeframe regime, RSI, MACD, stochastic, Bollinger bands, ADX, volume, market structure, funding, BTC regime and an order-book snapshot into a weighted confluence score.

An entry must clear all applicable gates:

- Minimum confluence score, confirmation count, trigger weight and direction edge.
- Spread, thin-side book-depth and ATR-volatility limits.
- Funding, BTC-regime and stand-aside filters.
- Risk limits, position limits, cooldowns and trade budgets.

All eligible symbols are scanned and ranked; the strongest allowed setup is selected rather than trading in configuration order.

## Execution and fills

With `PostOnlyEntries=false`, the bot crosses the visible spread with a bounded limit: buys at ask and sells at bid. With `PostOnlyEntries=true`, it rests at the near touch and tracks passive fill quality.

For Bybit, a public WebSocket maintains a local 50-level order book. It applies snapshots and deltas, reconnects with backoff, and sends heartbeats. A missing or stale stream book automatically falls back to REST, so a WebSocket outage cannot turn into a blind entry. The private Bybit stream separately wakes the loop after order, fill and position updates; REST still reconciles the resulting state.

Passive entries also pass a maker-quality gate. The intended touch must have enough USDT depth, the order cannot consume more than the configured fraction of that queue, the five-level imbalance must support the intended direction, and the book and scan must be fresh. These checks apply only to post-only orders and are configurable under `Trading.Maker*`.

The target must cover the real round-trip fee, the whole observed spread for a crossing entry, and `Risk.MinNetEdgePct`. Prices and amounts are snapped to venue rules before submission. Every order has a client order ID.

Timed-out entries are cancelled carefully: a cancellation acknowledgement is not treated as a final outcome. The bot waits for the final state so a partial fill during cancellation remains risk-accounted and managed.

## Position management

Each entry includes a wider exchange-side hard stop. The live manager enforces a tighter ATR-based stop and target, with optional fee-aware break-even, trailing stop, partial take-profit, scratch exit, time stop and older-losing-position exit when the higher-timeframe regime opposes it.

Exit requests are idempotent and back off on transient failure. If an exchange refuses a close for account reasons, the bot pauses new entries on that venue, keeps tracking the open position, retries slowly and alerts Telegram.

## Risk controls

RiskManager is separate from signal generation and applies:

- Stop-distance sizing with per-position and total-notional ceilings.
- Position, same-direction and per-market limits.
- Per-market cooldown plus hourly and daily entry budgets.
- Session and peak-drawdown cutouts.
- Optional daily-profit risk reduction or lock.
- Consecutive-loss halt/cooldown and margin-call detection.

There is also a per-market loss brake. By default, three consecutive realised
losses on one symbol stop new entries on that symbol for six hours, while exits
and other markets continue to operate. The brake is persisted across redeploys.

Risk state persists on disk. `ClearHaltOnBoot=true` is now one-shot: it performs one deliberate reset, records that fact, and cannot wipe hard halts after every Railway restart. Toggle it false, then true, for another intentional reset.

## Logs, journal and Telegram

Logs show decisions, order lifecycle, position state and risk events. Every closed trade is appended to a JSON-lines journal with realised PnL, fees and entry context: score, components, ATR, spread, funding, regime, time, risk scale, experiment label and realised entry slippage. Use it for strategy review by market, hour, component and exit reason.

`Trading.ExperimentId` is a label for a fixed configuration, for example `baseline-v1` or `risk-0.50-v1`. Change it whenever a rule, timing or risk configuration changes; the report keeps results separate instead of blending incomparable periods. The daily Telegram digest is enabled by default and includes the experiment label. Send `/report` to the Telegram bot for the whole-journal summary by experiment and market; no startup flag or Railway shell is required. `--journal-report` remains available for local export.

With `Trading.ShadowSignalsEnabled=true`, the bot also writes exactly one observation per market per fresh closed bar to `Trading.ShadowSignalPath`. It records eligible signals and the reason for every veto (signal, sizing or maker quality), but never submits an order or changes selection. It is the evidence set for deciding whether a gate improves results; it does not claim a rejected signal would have filled or won.

On Railway, add a service volume mounted at `/app/data`. The default journal, shadow observations, session state and position state already use that directory, so no environment variable is needed. Without the volume they remain enabled but are lost when Railway redeploys the container.

| Command | Action |
|---|---|
| `/status` | Mode, risk state and latest account snapshot |
| `/positions` | Open positions with stop/target and unrealised PnL |
| `/pause` | Stop new entries; keep exits managed |
| `/resume` | Re-enable entries after review |
| `/restart` | Graceful restart |
| `/version` | Running build and commit |

Use `/pause`, rather than DryRun, when positions are open. DryRun suppresses all outgoing orders, including bot-managed exits.

## Testing strategy changes

`--backtest` replays the live signal and shared exit rules over candles. It is useful for comparing configurations, but it does not model queue position, historical order books, partial fills, tick-level stop behavior or full slippage.

Before lowering the timeframe or increasing trade frequency:

1. Use actual maker/taker fees and conservative spread/slippage costs.
2. Use separate in-sample and out-of-sample periods.
3. Review the trade journal by market, hour, trigger and exit reason.
4. Start with small caps and compare realized execution with the model.

For the current strategy, better execution quality and a demonstrated post-cost edge are more valuable than simply creating more trades.

## Risk ramp to 2%

`Risk.RiskPerTradePct` is the live sizing value. `Risk.TargetRiskPerTradePct` is an explicit ceiling and roadmap target, capped by validation at 2%. The default remains 0.25% because the current notional cap can make actual risk substantially lower than the configured percentage; increasing the percentage alone does not produce a genuine 2% risk position.

Raise risk only after a fixed experiment has enough closed trades and the journal shows positive post-fee expectancy, acceptable realised entry slippage and no risk-control failures. A sensible sequence is 0.25% → 0.50% → 1.0% → 1.5% → 2.0%, with a new `ExperimentId` for each stage. Keep daily and peak drawdown limits coherent with the live stage: a 2% daily cutout means one fully-risked 2% loss can halt the bot for the day.

## Operational boundaries

- Railway deploys restart the in-process manager; exchange-side hard stops are the final protection during handover.
- Telegram long polling is exclusive: only one running instance should have commands enabled for a bot token.
- Use trade-only API keys; never give the bot withdrawal permission.
- Quote-adjusted risk equity does not remove foreign collateral from WhiteBIT cross margin.
- A successful deployment or backtest does not establish future profitability.
