# MASTER IMPLEMENTATION PROMPT (Optimized)
## Aggressive NSE Mid/Small/Microcap Momentum Trading Platform — ₹10,00,000

> This is an optimized rewrite of a prior AI-generated master prompt for the same system. Functional scope is preserved and, in places, tightened. What's new: an environment-disclosure step, a tranche-based execution protocol, resolved ambiguities, and a wiring-verification discipline — all drawn from lessons learned building a related C#/.NET 8 quant trading platform through 14+ delivery tranches.

---

# PART 0 — HOW TO EXECUTE THIS PROMPT (read first, follow throughout)

## 0.1 Role

Act as a **Principal Quantitative Developer, Senior C#/.NET 8 Architect, Algorithmic Trading Engineer, Portfolio/Risk Manager, Backtesting Engineer, Database Architect, and Production Systems Engineer** with 20+ years of institutional trading-system experience, building a production-grade systematic platform for a family account with ₹10,00,000 initial capital, trading NSE-listed midcap, smallcap, and selected microcap equities long-only.

This is not a toy, POC, pseudo-code exercise, or simplified demo. Build it as if it will manage real family capital — because it eventually will.

## 0.2 Environment self-disclosure — do this before writing any code

State plainly, before Tranche 0:

- Do you have a working `.NET 8 SDK` you can invoke (`dotnet build`, `dotnet test`)? Yes/No.
- Do you have outbound network access to: `nuget.org` / `api.nuget.org` (package restore), `query1.finance.yahoo.com` / `finance.yahoo.com` (historical data), `api.kite.trade` (Zerodha)? Answer per-domain, not as a single yes/no — sandboxes frequently allow some and block others.
- If NuGet restore is unavailable: default to **hand-rolled implementations** for anything you'd otherwise pull from a package (indicators, JSON parsing via `System.Text.Json` which ships with the SDK is fine — the risk is third-party NuGet packages specifically). Do not silently substitute a simplified version of an indicator and call it equivalent; hand-roll it fully and unit-test it against known reference values.
- If you cannot compile: say so explicitly in every delivery. Code must still be production-quality and hand-traced for correctness, but never claim "build succeeds" or "tests pass" without having actually run them. This distinction — implementation correctness vs. verified-by-execution — must be visible in every tranche's delivery notes, not just a final report.
- If you cannot reach Yahoo Finance or Kite Connect: implement the adapters completely against their documented contracts, ship deterministic fake/mock implementations for testing, and flag both adapters as **UNVERIFIED against the live API** until the user runs them in an environment with network access and reports back.
- Re-state this disclosure if execution moves to a different session/environment with different capabilities — don't assume prior tranches' constraints still apply.

## 0.3 Execution protocol — build in tranches, not in one shot

Do not attempt this in a single response. Work in the numbered tranches defined in **Part 9 (Roadmap)**. After each tranche:

1. Deliver against the fixed checklist in §9.1 (objective, architecture, full source, DB changes, config, tests, run instructions, acceptance criteria, known limitations).
2. Run **wiring verification** (§9.2) — confirm every component built in this tranche is actually invoked from the live decision path, not just added as a standalone, untested-in-context class. A component that compiles but is never called by anything is not "done."
3. Stop and wait for confirmation ("continue") before starting the next tranche, unless the user has explicitly asked for the whole roadmap to run unattended.
4. Append to a living `docs/gap-log.md` — every stub, mock, unverified integration, or deferred requirement, updated every tranche. Do not defer all disclosure to one "final completion report"; gaps found mid-build get logged when found, not rediscovered later by audit.

At two checkpoints — after Tranche 5 (strategy/risk core complete) and after Tranche 12 (paper trading complete) — stop and produce an explicit **completeness audit**: re-read this prompt section by section and confirm what's actually wired versus what merely exists. This catches "built but not connected" gaps before they compound.

## 0.4 Non-negotiable operating principles

```text
SURVIVAL
    ↓
RISK CONTROL
    ↓
REPEATABLE EDGE
    ↓
CAPITAL COMPOUNDING
    ↓
AGGRESSIVE GROWTH
```

- Never weaken a risk control to improve a backtest number.
- Never fabricate data (Yahoo fundamentals, ASM/GSM surveillance status, sector classification, market-cap bucket) — mark unavailable data `Unknown`, never assume "safe" or "positive" by default.
- Never claim a strategy is profitable merely because the backtester ran without errors. Implementation correctness and strategy profitability are separate claims — do not conflate them in any report.
- Never claim live trading or a broker integration is tested without real credentials and a real (or at minimum sandboxed) API call having actually happened.
- Prefer a robust region of parameters over one "perfect" backtested parameter set.

---

# PART 1 — TECHNOLOGY & ARCHITECTURE

## 1.1 Stack

- C#, .NET 8, nullable reference types enabled, async/await throughout I/O
- Generic Host / DI (`Microsoft.Extensions.Hosting/Configuration/DependencyInjection/Logging`)
- Serilog (console + rolling file + SQLite system-event sink)
- `Microsoft.Data.Sqlite`
- `HttpClientFactory` (never `new HttpClient()` per request)
- Polly (or hand-rolled retry/backoff if package restore is unavailable — see §0.2)
- Indicators: prefer **hand-rolled, unit-tested against reference values** as the default; use `Skender.Stock.Indicators` only as an optional cross-check when package restore is confirmed available. Do not make the build depend on it.
- REST/HTTPS for Yahoo Finance history; Zerodha Kite Connect official SDK/API for live execution
- No paid licenses required for the core platform. No unnecessary commercial dependencies.

## 1.2 Solution structure

```text
TradingPlatform
├── Core            (Domain, Models, Enums, Interfaces, Configuration, Exceptions)
├── Data            (YahooFinance, SQLite, MarketDataCache, DataNormalization)
├── Indicators      (Trend, Momentum, Volatility, Volume, RelativeStrength, VCP)
├── Strategy        (Universe, MarketRegime, StockScoring, Entry, Exit, Pyramiding, SignalEngine)
├── Risk            (PositionSizing, PortfolioRisk, Drawdown, Correlation, Exposure, CircuitBreaker)
├── Backtesting     (Engine, PortfolioSimulator, ExecutionSimulator, Slippage, Commission, WalkForward, Optimization, MonteCarlo)
├── Execution       (PaperTrading, Zerodha, OrderManager, PositionReconciliation)
├── Scanner         (UniverseScanner, RankingEngine, ScheduledScanner)
├── Reporting       (Performance, TradeAnalytics, RiskAnalytics, Export)
├── Api             (thin REST surface — dashboard-ready, see §7.5)
└── Application     (Backtest, Scan, PaperTrade, LiveTrade, Portfolio — CLI entry points)
```

Keep interfaces provider-agnostic:

```csharp
IHistoricalMarketDataProvider   // YahooFinanceHistoricalDataProvider today, swappable later
IExecutionProvider              // BacktestExecutionProvider / PaperExecutionProvider / ZerodhaExecutionProvider
```

**Architectural centerpiece — build this deliberately, not as an afterthought:** a single shared decision-loop component (e.g. `StrategyStepEngine`) that runs identically across Backtest, Paper, and Live. Only its injected `IHistoricalMarketDataProvider`/`IRealtimeQuoteProvider`, `IExecutionProvider`, and clock differ per mode. This is the only way to guarantee the live system runs the exact strategy that was backtested — treat any divergence between modes as a defect, and cover it with a parity test that asserts identical decisions given identical inputs.

Do not create dozens of projects. Keep the project count aligned to the tree above.

## 1.3 Dependency-risk note

Package restore commonly fails in sandboxed agent environments (no path to `nuget.org`). Where this applies:
- Indicators, retry/backoff, and JSON handling: hand-roll (SDK-provided `System.Text.Json` is fine; third-party NuGet is the risk).
- Zerodha SDK: if the official NuGet package can't be restored, implement the HTTP contract directly against documented endpoints behind `IZerodhaClient`, so swapping in the official SDK later is a drop-in change.
- Document every substitution in `docs/gap-log.md`.

---

# PART 2 — CAPITAL, UNIVERSE & ELIGIBILITY

## 2.1 Capital & config

Initial capital ₹10,00,000. Nothing below is a hard-coded literal — all live in strongly-typed `Options` classes (§7.4):

```json
{
  "Portfolio": {
    "InitialCapital": 1000000,
    "MaxPositions": 10,
    "MinimumCashPercent": 10,
    "MaxSinglePositionPercent": 15,
    "ExceptionalPositionPercent": 20,
    "MaxSectorExposurePercent": 25,
    "MaxMicrocapExposurePercent": 35
  }
}
```

## 2.2 Investment universe & symbol mapping

Long-only NSE: midcaps, smallcaps, selected microcaps. Configurable CSV/SQLite universe, each instrument carrying: `Symbol, NseSymbol, YahooSymbol, ISIN, CompanyName, Sector, Industry, MarketCap, AverageDailyTurnover, LotSize, Exchange, Active`.

**Market-cap bucket classification — resolved ambiguity:** "midcap/smallcap/microcap" must not be an arbitrary absolute-rupee cutoff you invent. Use the semi-annual AMFI/SEBI market-cap-rank classification (rank 1–100 = large, 101–250 = mid, 251+ = small; treat microcap as a configurable sub-threshold within smallcap by absolute market cap, e.g. < ₹5,000cr). Store these as **point-in-time snapshots** (`UniverseClassificationSnapshots` table, effective date range) so a backtest in 2019 uses 2019's classification, not today's. Where the classification file for a historical period is unavailable, mark the instrument's bucket `Unknown` for that period rather than inferring it from current-day market cap.

**Sector classification — same treatment:** Yahoo does not reliably provide an NSE-consistent sector taxonomy. Introduce `ISectorClassificationProvider` (manual/CSV/SQLite ingestion, same pattern as the surveillance provider below), with `Unknown` as the explicit fallback — never silently default a stock into a sector that happens to make a diversification check pass.

`SYMBOL.NS` is the common Yahoo suffix but not universal — build a real symbol-mapping layer, don't assume it.

## 2.3 Universe eligibility filter

```text
Active = true
AverageDailyTurnover >= configured threshold
Price >= configured minimum
Sufficient historical data
Not permanently suspended
Not invalid Yahoo symbol
```

Risk flags: `ASM, GSM, TradeToTrade, SurveillanceFlag, CorporateActionRisk, LiquidityRisk, DataQualityRisk`. Do not fabricate ASM/GSM from Yahoo. Interface:

```csharp
ISurveillanceDataProvider
```

Manual/CSV/SQLite ingestion initially. Unknown status stays `Unknown`, never defaults to "Safe."

## 2.4 Liquidity rules

```text
Average Daily Turnover, 20D avg volume, 20D avg traded value, median traded value
PositionValue <= MaxParticipationPercent * AverageDailyTradedValue   (default 2%, configurable)
```

Backtester must reject fills that historical liquidity couldn't have actually supported.

---

# PART 3 — STRATEGY ENGINE

## 3.1 Market regime engine

`IMarketRegimeService` determines deployable capital using configurable signals: Nifty 500 trend, above/below 50 & 200 DMA, breadth, advance/decline, % of universe above 50/200 DMA, small/midcap relative strength, volatility, breakout success rate.

```text
RegimeScore 0–100 → Aggressive / Normal / Cautious / Defensive / Emergency
80–100 → 100% exposure | 65–79 → 80% | 50–64 → 50% | 35–49 → 25% | <35 → 0–10%
```

All thresholds configurable.

## 3.2 Stock scoring engine (100 points)

```text
Relative Strength        20      Volume Accumulation   15
Price Momentum           15      Earnings Acceleration 15
VCP Quality               10      Revenue Acceleration  10
Trend Structure            5      Institutional Activity 5
Sector Strength             5
```

Do not use fundamental fields Yahoo doesn't reliably provide historically without disclosing the limitation. Interfaces `IFundamentalDataProvider` / `IInstitutionalActivityProvider` allow marking components unavailable — missing data must never silently default to a positive score. Output: `TotalScore, TechnicalScore, FundamentalScore, RelativeStrengthScore, VolumeScore, VcpScore, SectorScore, DataQualityScore`.

## 3.3 Relative strength

Against Nifty 500, sector benchmark, and midcap benchmark where applicable. Periods `RS20/RS50/RS90/RS180`, normalized 0–100. System prefers stocks substantially outperforming their benchmark.

## 3.4 Technical indicators

- **Trend:** HMA fast/slow, Supertrend, SMA 20/50/200
- **Momentum:** RSI, MACD, ADX
- **Volatility:** ATR, ATR%, historical volatility
- **Volume:** volume SMA, relative volume, expansion, delivery % (if a reliable source is added later)
- **Price structure:** swing highs/lows, breakout levels, support/resistance, HH/HL/LH/LL

All hand-rolled per §1.1/§1.3, unit-tested against known reference values (e.g., a published ATR/RSI worked example).

## 3.5 VCP detection

Real detector, not a label slapped on any consolidation: prior uptrend, consolidation, progressive volatility contraction, declining volume, tighter ranges, higher lows, resistance/pivot. Outputs `VcpScore, NumberOfContractions, ContractionRatios, PivotPrice, BaseLength, AverageVolumeDuringBase, VolumeDryUpScore`. Unit tests against synthetic price series with known contraction patterns.

## 3.6 Breakout engine

```text
Close > Pivot AND Volume >= 1.5×20D avg volume AND RelativeStrength >= threshold
AND ADX >= 20 AND Price > HMA-fast AND HMA-fast > HMA-slow AND Regime allows new positions
```

Breakout signal uses only data available at the decision timestamp — no look-ahead.

## 3.7 Staged entry model

```text
Stage 1: 40% — initial breakout signal
Stage 2: 30% — after +2–3% favorable move, trend still valid, no stop violation
Stage 3: 30% — after successful breakout retest, support holds, momentum constructive
```

Never pyramid into a loser. Never average down.

## 3.8 Execution-timing convention — resolved ambiguity

The original prompt left "signal timing → order timing" as an open choice. **Default, unless the user overrides it:** signal is evaluated on the confirmed daily close; the resulting order is eligible for the **next session's open**, subject to the slippage model in §4.3. `EntryPrice == Close` is never assumed. Document this as `Backtest.SignalToOrderLag = "NextOpen"` in config, and make it configurable for the 60m/15m refinement layers described in §7.3.

---

# PART 4 — RISK & POSITION MANAGEMENT

## 4.1 Position sizing — resolved ambiguity on constraint precedence

```text
RiskAmount   = NAV × RiskPercent          (default 1%, max 1.25%, configurable)
RiskPerShare = EntryPrice − StopPrice
PositionQty  = RiskAmount / RiskPerShare
```

**If `RiskPerShare <= 0`** (e.g., entry gapped through the stop before fill) — reject the trade outright; never sign-flip or floor it to a nominal value.

Round to lot/tradability constraints, then compute the final size as the **minimum** of: risk-based quantity, max single position %, liquidity constraint (§2.4), sector constraint, microcap exposure constraint, cash constraint, regime exposure multiplier, portfolio-heat headroom. Log which constraint(s) actually bound for every sized trade — this is required for the unit tests in §8.1, which must independently prove each constraint binds when it's the tightest one.

## 4.2 Initial stop, portfolio heat, sector/correlation, microcap risk

```text
InitialStop = min(technical swing-low invalidation, 2.0–2.5 × ATR)  — configurable selection rule
Store: InitialStop, StopDistance, RiskPerShare, InitialRiskAmount, RMultiple

PortfolioHeat = Σ(OpenPositionRisk / CurrentNAV)   — max 8%, configurable, blocks new entries above it
MaxSectorExposure = 25% (configurable)
Rolling correlation window = 60 trading days — reduce effective diversification credit for correlated clusters;
    ten highly-correlated names must not count as ten independent bets

MaxAggregateMicrocapExposure = 35%; microcap entries additionally require:
    liquidity pass, TechnicalScore >= 80, FundamentalScore >= threshold,
    no critical surveillance flag, no severe data-quality issue — cannot bypass portfolio risk rules
```

## 4.3 Exit engine & trailing stop

Four independent exit mechanisms: **(A)** stop loss — immediate; **(B)** momentum failure (RS deterioration + HMA breakdown + volume distribution, configurable); **(C)** fundamental failure (severe deterioration where fundamental data is available); **(D)** market-regime failure (portfolio-level exposure reduction).

```text
Trailing ladder: Initial stop → Breakeven → ATR trailing → Swing-low trailing → HMA/Supertrend trailing
Default transitions: +1R → breakeven, +2R → active trailing, +3R → aggressive winner protection
```

Never auto-take-profit at a fixed +10%/+20% — let winners run.

## 4.4 R-multiple analytics

Every trade: `InitialRisk, RealizedPnL, RMultiple, MaximumFavorableExcursion, MaximumAdverseExcursion`. Aggregate: `Average R, Median R, Win Rate, Average Winner, Average Loser, Profit Factor, Expectancy`.

## 4.5 Drawdown engine & loss limits

```text
Track: Peak NAV, Current NAV, Drawdown %, Max Drawdown, Drawdown Duration, Recovery Duration

Circuit breakers:
0–10%   full aggression      20–25%  reduce risk 40%
10–15%  normal/reduced       25–30%  half exposure
15–20%  reduce risk 20%      30–35%  capital preservation
                              35%+    emergency mode
                              40%     TRADING SHUTDOWN — no new trades, explicit manual reset required

Daily loss limit 2%   → stop new trades that day
Weekly loss limit 4%  → reduce risk
Monthly loss limit 8% → defensive mode
```

All thresholds configurable.

---

# PART 5 — BACKTESTING, DATA & VALIDATION

## 5.1 Backtesting engine

Simulates historical data, signals, portfolio, cash, orders, fills, slippage, brokerage, taxes/charges, position sizing, stops, pyramiding, liquidity, partial fills, and corporate actions where data permits. Never uses future information.

## 5.2 Yahoo Finance data provider & cache

`IHistoricalMarketDataProvider` → `YahooFinanceHistoricalDataProvider`: HTTP via `HttpClientFactory`, retries, exponential backoff, rate limiting, timeout/cancellation, response validation, JSON parsing, timezone normalization, missing-candle detection, caching, dedup.

Cache in SQLite (`HistoricalBars`: `Id, Symbol, Timestamp, Open, High, Low, Close, AdjustedClose, Volume, Source, Timeframe, CreatedAt`; unique index `(Symbol, Timestamp, Timeframe)`). Flow: check cache → determine missing ranges → download only missing ranges → validate → persist.

## 5.3 Data quality & corporate actions

Detect missing/duplicate candles, zero volume, invalid OHLC relationships, negative values, extreme unexplained gaps, timezone issues, splits, corporate-action distortions. **Never silently "fix" suspicious data — flag it.**

**Resolved:** raw OHLC is used for stop-loss/execution simulation; adjusted close is used for return calculation. Never use adjusted close for stop-loss simulation. Document which series feeds which calculation, per component.

## 5.4 Execution model, slippage, transaction costs

Support market/limit/stop/stop-loss orders, partial fills, slippage, transaction costs, liquidity limitations. Slippage models: `FixedBps`, `Percentage`, `LiquidityBased` (default for backtests; dynamically widens for microcaps as liquidity deteriorates). Transaction costs (brokerage, STT, exchange charges, GST, SEBI charges, stamp duty) live in config, not hard-coded — document that current rates must be verified before live deployment.

## 5.5 Walk-forward, parameter optimization, Monte Carlo

```text
Walk-forward: rolling Train/Validate/Test windows (e.g. 3y/6m/6m). Never optimize on the full
    dataset and report those results as out-of-sample.

Parameter optimization: grid search over RiskPercent, ATR multiplier, RSI/ADX thresholds, volume
    multiplier, VCP score threshold, breakout threshold, HMA periods, trailing-stop params, regime
    thresholds. Selection uses a composite score (return, drawdown, Sharpe, stability, trade count,
    out-of-sample performance) — never CAGR alone.

Monte Carlo: randomize trade order, win/loss sequence, slippage/execution variation. Report 5th/25th/
    median/75th/95th percentile ending capital, P(DD>20/30/40%), probability of ruin.
```

## 5.6 Bias & parity requirements — mandatory, not optional

- **No look-ahead:** never use future earnings/market cap/index membership/sector classification/corporate actions/closing prices unless the dataset is genuinely point-in-time.
- **Survivorship bias:** the backtester must support historical universe snapshots (§2.2). Where point-in-time constituents are unavailable, every backtest report header must carry an explicit `SURVIVORSHIP-BIASED` warning — this is not optional documentation buried in a README, it's a required field on the report object itself.
- **Data-source limitations:** Yahoo does not reliably provide NSE corporate-action history, real-time exchange-grade data, point-in-time fundamentals, historical surveillance status, or order-book data. Model each limitation explicitly; never fabricate.
- **Live/backtest parity:** the shared `StrategyStepEngine` (§1.2) is how this is enforced structurally, not just documented. Only `DataProvider`, `ExecutionProvider`, and `Clock` differ across Backtest/Paper/Live.

## 5.7 Anti-overfitting discipline

Actively detect and flag: overfitting, too few trades, parameter instability, excessive drawdown, single-stock/single-year/bull-market dependence, survivorship bias, look-ahead bias. A candidate is rejected if `MaxDD > 40%` or if attractive performance disappears out-of-sample.

---

# PART 6 — LIVE TRADING

## 6.1 Modes & safety defaults

```text
DRY RUN / PAPER / LIVE — explicit modes, default PAPER.
Activating live requires BOTH: Mode = "Live" AND AllowLiveTrading = true.
Even then: validate broker connection before submitting any order.
```

## 6.2 Zerodha integration

```csharp
IZerodhaClient
IOrderExecutionService
IPositionService
IAccountService
```

Never hard-code API keys/secrets/tokens — environment variables or .NET User Secrets. **Verification requirement:** before writing the adapter, verify current official Kite Connect authentication/order semantics against live documentation. If the executing agent has no network access to `kite.trade` (see §0.2), implement fully against the last-known documented contract, flag the adapter **UNVERIFIED**, and require the user to do a manual doc-diff pass before enabling `AllowLiveTrading`.

Handle gracefully: auth failure, expired token, rate limiting, order rejection, invalid instrument, insufficient funds, market closed, partial fill, network failure, unknown order state.

## 6.3 Order flow, validation, state machine

```text
Signal → Risk Engine → Portfolio Constraint Engine → Order Validation → Order Manager
       → Zerodha Adapter → Broker → Order Update → Reconciliation → SQLite
```

Pre-trade validation checks (any failure → reject with detailed reason): market open, instrument valid, symbol mapped, sufficient cash, position/sector/microcap limits, portfolio heat, drawdown status, daily loss limit, order quantity, liquidity, duplicate order, existing position, existing pending order.

Order states: `Created, Validated, Submitted, Acknowledged, Open, PartiallyFilled, Filled, Cancelled, Rejected, Failed, Unknown`. Never assume a fill because submission succeeded.

## 6.4 Reconciliation & kill switch

At startup and every trading cycle: fetch broker positions/open orders/order history/funds, compare against SQLite. Any mismatch → **halt trading, alert, require manual intervention** — never auto-guess a fix.

Kill switch sources: manual, drawdown, daily loss, broker error, data error, reconciliation error, repeated order rejection, abnormal slippage, connectivity failure. When active: no new orders (optionally allow risk-reducing exits only).

## 6.5 Restart recovery

On restart while live: load DB state → connect to broker → reconcile positions/orders/fills → reconstruct portfolio → validate risk → **only then** resume. Never blindly resume order generation after a crash.

---

# PART 7 — PLATFORM CONCERNS

## 7.1 Scheduler & scanner

Hosted services / background workers (not raw `Task.Delay()` loops without cancellation/fault handling):

```text
Pre-market:  data refresh, universe validation, reconciliation, regime calc
Intraday:    scanner, signal eval, risk checks, order mgmt, position monitoring
Post-market: reconciliation, performance, DB snapshot, daily report
```

Scanner runs every 15 min (configurable) during market hours: `Universe → Liquidity → Regime → Indicators → RS → Volume → VCP → Fundamental quality → Risk filter → Score → Rank`. Output top 30 with `Rank, Symbol, Price, MarketCap, Sector, Score, RS/Momentum/Volume/VCP Score, ADX, RSI, ATR, Pivot, BreakoutStatus, DistanceFromPivot, SuggestedEntry, SuggestedStop, Risk, SuggestedPositionSize`.

## 7.2 Database, journal, logging, alerting

Minimum SQLite tables: `Instruments, HistoricalBars, Indicators, MarketRegimes, Signals, Orders, OrderEvents, Fills, Positions, PortfolioSnapshots, Trades, TradeLegs, RiskEvents, DrawdownEvents, ScannerResults, BacktestRuns, BacktestTrades, OptimizationRuns, SystemEvents, UniverseClassificationSnapshots` (§2.2). `CreatedAt/UpdatedAt/CorrelationId` where relevant; index `Symbol+Timestamp, TradeDate, OrderId, SignalId, BacktestRunId`.

Immutable trade journal — every completed trade preserves signal, entry, initial stop, position size, pyramiding events, exit, reason, regime, score, risk-at-entry, MFE, MAE, R-multiple, PnL. Never overwritten.

Serilog structured logging (Debug/Info/Warning/Error/Fatal) to console + rolling file + SQLite system-event log. Never log secrets/tokens/passwords. `INotificationService` (console now; Telegram/Email/Slack adapters later) for signal/order/fill/rejection/stop/drawdown/kill-switch/reconciliation-failure/system-failure events.

## 7.3 Configuration, CLI, multi-timeframe

Strongly-typed options: `PortfolioOptions, RiskOptions, StrategyOptions, MarketRegimeOptions, YahooFinanceOptions, ZerodhaOptions, BacktestOptions, ScannerOptions, NotificationOptions, DatabaseOptions`. No magic numbers.

```bash
dotnet run -- scan | backtest [--from --to] | optimize | walkforward | montecarlo | paper | live | portfolio | reconcile
```

`--help` required. Multi-timeframe support: Daily (primary), 60m, 15m, 5m — weekly=market bias, daily=stock setup, 60m=entry refinement, 15m=execution (per §3.8's default, 15m/60m are optional refinements, not required for v1). Avoid unnecessary high-frequency processing.

## 7.4 Testing strategy

- **Unit:** ATR sizing, position sizing (including the constraint-precedence cases in §4.1), VCP detection, breakout detection, relative strength, market regime, portfolio heat, drawdown, sector exposure, pyramiding, stop calculation.
- **Integration:** SQLite, Yahoo parser, order state machine, broker adapter, reconciliation.
- **Backtest regression:** deterministic synthetic datasets (required regardless of network access — do not depend on live Yahoo data for regression tests), stable expected output.
- **Parity tests:** `StrategyStepEngine` produces identical decisions across Backtest/Paper/Live given identical inputs.
- **Failure tests:** Yahoo timeout/429/malformed JSON/missing bars, SQLite locked, broker timeout/rejection/partial fill/duplicate order/unknown status, network disconnect, restart, unexpected position, token expiration — system must fail safely in each case.

## 7.5 Security, production layout, exports, dashboard-readiness, performance, observability

Never commit API keys/secrets/tokens/passwords. Environment variables / User Secrets / secret manager; `.gitignore`; `.env.example` with placeholder values. Directories: `/data /logs /config /reports /backtests /exports` (configurable paths).

Export reports as CSV/JSON/HTML: `BacktestReport, TradeReport, PortfolioReport, RiskReport, ScannerReport, OptimizationReport, MonteCarloReport`.

No frontend required now, but keep domain logic UI-independent and expose a thin API surface (`/api/portfolio /positions /orders /signals /scanner /risk /performance /backtests`) so a future dashboard is a pure consumer.

Performance: async I/O, bounded concurrency (configurable limit, no unbounded parallel HTTP), caching, batch DB writes, minimal repeated indicator recomputation, incremental data updates.

Observability: expose data-fetch latency, scanner duration, signals generated, orders submitted/failed, DB latency, broker latency, current portfolio heat/drawdown/regime.

## 7.6 Versioning & reproducibility

Every signal/trade stores `StrategyVersion, ConfigurationVersion` (e.g. `AggressiveMomentumV1`). Every backtest run persists a full config snapshot at start: `BacktestRunId, StrategyVersion, ConfigurationHash, DataVersion, StartDate, EndDate` — results must be reproducible from the snapshot alone.

---

# PART 8 — REPORTING & ACCEPTANCE

## 8.1 Expected backtest output

```text
========================================================
 AGGRESSIVE NSE MOMENTUM FUND
========================================================
Initial Capital : ₹10,00,000        Final Capital   : ₹XX,XX,XXX
Net Return      : XX%               CAGR            : XX%
Max Drawdown    : XX%               Sharpe          : X.XX      Sortino  : X.XX
Win Rate        : XX%               Profit Factor   : X.XX      Expectancy: X.XX R
Trades          : XXX

Worst Month : XX%   Best Month : XX%   Avg Winner : X.XXR   Avg Loser : X.XXR
Portfolio Heat — Average: X.X%  Maximum: X.X%
Regime — Bull: XX%  Normal: XX%  Bear: XX%
[SURVIVORSHIP-BIASED — see §5.6]  ← only if point-in-time universe unavailable
========================================================
```

## 8.2 Strategy acceptance criteria

Minimum research report: CAGR, Max DD, Sharpe, Sortino, Profit Factor, Expectancy, trade count, win rate, average R, worst year/month, recovery time, Monte Carlo DD, out-of-sample CAGR/DD. Rejected if `MaxDD > 40%` or if performance evaporates out-of-sample (§5.7). High CAGR alone never validates a strategy.

## 8.3 Capital growth simulation & risk-of-ruin

Hypothetical growth from ₹10 lakh at 20/30/40/50/60/80/100% annual return over 3/5/10 years — clearly labeled as mathematical scenarios, not forecasts. Monte Carlo probability of exceeding 20/30/40/50% drawdown, explicitly including P(exceeding the permitted 40% shutdown threshold).

---

# PART 9 — ROADMAP: TRANCHE-BY-TRANCHE EXECUTION

## 9.1 Per-tranche deliverable checklist (apply to every tranche below)

```text
[ ] Objective (one paragraph)
[ ] Architecture / component diagram for this tranche
[ ] Full source (no pseudocode)
[ ] DB schema changes (migrations or EnsureCreated additions)
[ ] Config additions (Options classes + appsettings entries)
[ ] Unit/integration tests for everything new
[ ] Run instructions (exact commands)
[ ] Acceptance criteria (how to verify this tranche works)
[ ] Known limitations / gap-log.md entries
[ ] Wiring verification (§9.2)
```

## 9.2 Wiring verification (mandatory, every tranche)

Before marking a tranche done, answer explicitly: *is every new component actually called from the live decision path (scanner, `StrategyStepEngine`, or a scheduled job), or does it just compile standalone?* If a component isn't wired in yet by design (e.g., options architecture before options trading is in scope), say so explicitly — don't let it pass silently as "delivered."

## 9.3 Tranches

```text
Tranche 0  — Solution structure, domain models, configuration, logging, environment disclosure (§0.2)
Tranche 1  — SQLite schema + repositories (core tables from §7.2)
Tranche 2  — Yahoo Finance historical provider + cache + data-quality checks (§5.2–5.3)
Tranche 3  — Indicators (hand-rolled, §3.4) with reference-value unit tests
Tranche 4  — Universe, symbol mapping, eligibility filter, sector/market-cap-bucket
             providers (§2.2–2.3)
Tranche 5  — Market regime engine + stock scoring engine + relative strength (§3.1–3.3)
             → CHECKPOINT: completeness audit against Parts 2–3
Tranche 6  — VCP detection + breakout engine + staged entry model (§3.5–3.7)
Tranche 7  — Position sizing, stops, portfolio heat, sector/correlation/microcap
             risk (Part 4, minus exits)
Tranche 8  — Exit engine + trailing stop ladder + R-multiple analytics (§4.3–4.4)
Tranche 9  — Drawdown engine, circuit breakers, daily/weekly/monthly loss limits (§4.5)
Tranche 10 — Backtesting engine + execution/slippage/cost models, wired to a real
             StrategyStepEngine driving signals/risk/sizing end-to-end (§5.1, §5.4, §1.2)
             — this is the first tranche where a full backtest can actually run
Tranche 11 — Performance reporting + regime attribution + expected-output report (§7.6 wait,
             §8.1, Part 8 reporting)
Tranche 12 — Walk-forward + parameter optimization + Monte Carlo (§5.5)
             → CHECKPOINT: completeness audit against Parts 3–5, confirm no orphaned components
Tranche 13 — Scanner + scheduler (§7.1)
Tranche 14 — Paper trading: StrategyStepEngine driving a paper execution provider and a
             realtime/replay quote source, off by default
Tranche 15 — Zerodha adapter (§6.2) — flagged UNVERIFIED per §0.2/§6.2 unless real network
             access and credentials are available
Tranche 16 — Live order architecture: validation, state machine, order manager,
             reconciliation, kill switch, restart recovery (§6.1, §6.3–6.5)
Tranche 17 — REST API surface + dashboard-readiness (§7.5)
Tranche 18 — Production hardening: observability, CI config (written, may be unexecuted
             per §0.2), Docker (optional), full README, final completeness report
```

This ordering mirrors the original spec's phase list but folds testing and wiring verification into every tranche rather than treating them as separate late-stage phases (17→"production hardening" in the original). Adjust granularity if a tranche proves too large for a single response — smaller, verifiable tranches beat large unverifiable ones.

---

# PART 10 — DELIVERABLES & FINAL INSTRUCTIONS

## 10.1 Deliverables

```text
*.sln, *.csproj, Program.cs, src/, tests/, config/, scripts/, README.md,
appsettings.json, appsettings.Development.json, .env.example, .gitignore
```

README covers: architecture, setup, .NET 8 install, NuGet packages (and the hand-rolled substitutions per §1.3 if applicable), SQLite init, Yahoo config, historical data download, backtest, walk-forward, optimization, Monte Carlo, scanner, paper trading, Zerodha setup, live-trading activation, risk configuration, troubleshooting, security, known data limitations, production deployment.

## 10.2 Final production checklist

```text
[ ] Build succeeds (or explicitly disclosed as unverified per §0.2)
[ ] Nullable warnings addressed        [ ] Unit/integration tests pass (or unverified, disclosed)
[ ] SQLite init correct                 [ ] Yahoo fetch/cache/rate-limit work (or unverified)
[ ] Indicator calcs verified            [ ] VCP / regime / position-sizing / heat / drawdown tests pass
[ ] Backtest deterministic, no look-ahead
[ ] Walk-forward / Monte Carlo work     [ ] Paper trading works
[ ] Zerodha adapter tested or explicitly flagged UNVERIFIED
[ ] Live mode disabled by default       [ ] Reconciliation / kill switch / restart recovery work
[ ] Secrets excluded from source        [ ] Structured logging works
[ ] Reports generate                    [ ] README complete
[ ] gap-log.md reflects every disclosed limitation
```

## 10.3 Final instructions

Do not stop after producing architecture — implement the system. No pseudocode where production C# is expected. No fake Yahoo/Zerodha responses in production code paths (mocks belong in test code only, clearly separated). Never claim live trading is tested without real credentials. Never claim profitability from a backtest that merely executed without error.

At the end of the roadmap: build the solution, run all tests, run a sample backtest, run walk-forward, run Monte Carlo, run the scanner against the cached universe, run a paper-trading simulation, validate the Zerodha adapter against mocks (and live, if credentials are available), review every risk control and every failure/recovery path, fix compilation errors/warnings/test failures — or disclose precisely why any of that couldn't be verified in this environment — and produce a concise implementation summary plus exact run commands for every mode (data download, backtest, optimization, walk-forward, Monte Carlo, scanner, paper trading, reconciliation, live trading).

**Above all: never weaken risk controls to improve backtest returns.** The objective is aggressive long-term capital appreciation through concentrated NSE mid/small/microcap momentum, under strict portfolio-level risk control and a hard maximum-drawdown framework — survival and risk control come before edge, compounding, and growth, in that order.
