# TradingPlatform — Aggressive NSE Momentum Fund (AggressiveMomentumV1)

A C#/.NET 8 quantitative trading platform for a long-only ₹10,00,000 NSE
mid/small/microcap momentum strategy, built from the optimized master spec
(`Optimized-10L-NSE-Momentum-Trading-System-Prompt.md`). Backtest, scan,
paper, and live trading share the same strategy decision logic by
construction — only the data/execution/clock providers differ per mode.

**Status:** All 22 roadmap rows (Tranches 0-21) complete. Every command in
the CLI is genuinely implemented — `env`, `fetch`, `scan`, `backtest`,
`optimize`, `walkforward`, `montecarlo`, `paper`, `live`, `portfolio`,
`reconcile`, `serve` — backed by real SQLite persistence and a real REST
API. See `docs/roadmap.md` for the full tranche history and
`docs/gap-log.md` for every environment limitation, resolved ambiguity,
documented simplification, and bug caught along the way.

**Build verified (2026-09-13):** this codebase was originally built and
hand-traced across several sandboxes with no `dotnet` SDK and no way to
compile a single line of it — every earlier claim of correctness was
careful signature-by-signature review, not a passing build. That changed
this session: a real .NET 8 SDK (`8.0.131`, via `apt`) was available, and
the full solution was compiled and run for the first time. `dotnet build`
found 4 real, previously-undetectable bugs on the first attempt — a
merge artifact that left `Program.cs` (the CLI entry point) unable to
compile at all, a C# generic-constraint error in a repository query, a
missing interface member on a superseded fallback class, and three
missing assertion methods in the hand-rolled test framework that had
silently prevented two whole test files from ever compiling. All four are
fixed; **the solution now builds with 0 warnings/0 errors** in both Debug
and Release, and **the full hand-rolled test suite passes 636/636**. The
CLI (`env`, `portfolio`, `scan`, `backtest`, `serve`) and the REST API
(including the dashboard's static HTML) were exercised live against a
real on-disk SQLite database and real `libsqlite3` — a full staged-entry
VCP breakout scenario was seeded and run end-to-end through
`backtest`, producing a correctly-computed trade (entry, stop, exit,
R-multiple, exported CSV/JSON/HTML reports) and was then read back
through `/api/backtests/1/detail` over real HTTP with enums serializing
as strings, as intended. See `docs/gap-log.md`'s dated entry for this
session for the full account.

**What remains genuinely unverified before trusting this with money:**
this sandbox still has no outbound network access to Yahoo Finance or
Zerodha Kite Connect (confirmed live via `env` this session, same as
every prior session) — so the Yahoo historical-data provider and the
Zerodha execution adapter are implemented against documentation only and
have never exchanged a byte with the real services. Run `fetch` and a
`paper` cycle against live Yahoo data, and do a manual Kite Connect
doc-diff (flip `ZerodhaOptions.AdapterVerifiedAgainstLiveDocs` only after
that), in an environment with real network access before going further —
see `docs/gap-log.md` for the complete, itemized account of what that
means tranche by tranche.

## Quickstart

```bash
# Build everything (fully offline — see docs/gap-log.md)
dotnet build TradingPlatform.sln

# Show the CLI
dotnet run --project src/TradingPlatform.Application -- --help

# Live environment disclosure: dependency reachability + active substitutions
dotnet run --project src/TradingPlatform.Application -- env

# Run the hand-rolled test suite (no xunit/NUnit — see docs/gap-log.md)
dotnet run --project tests/TradingPlatform.Tests
```

> **Before your first run with real data, read [`docs/first-real-run.md`](docs/first-real-run.md)** — what healthy output looks like, the red flags, and what to send back.

### A first real run, end to end

```bash
APP="dotnet run --project src/TradingPlatform.Application --"

# 1. Build the universe in SQLite from your CSV (one command family: `universe`) ...
$APP universe import --file universe.csv                 # stock details; duplicates/ISIN clashes are reported, not hidden
$APP universe import --kind marketcap --file marketcap.csv   # Symbol,MarketCap(₹ crore)  — the stock file usually has 0 here
$APP universe classify --auto --large-min 100000 --mid-min 30000 --small-min 5000   # ₹ crore cut-offs: use the CURRENT AMFI values
$APP universe surveillance --asm-file asm.csv --gsm-file gsm.csv --assume-unlisted-clear   # daily; see docs/data-import.md
$APP universe stats                                      # what is known / unknown / stale

#    ... then download daily OHLC for the WHOLE universe (+ benchmark indices) into the same SQLite DB.
#    Incremental and resumable; every failure is listed. Needs real network to Yahoo — this sandbox is blocked.
#    ^CRSLDX is Nifty 500 (BenchmarkOptions.BroadIndexSymbol) — verified via web search as a real Yahoo ticker.
#    NMIDCAP150.NS (MidcapIndexSymbol) is NOT verified against a live Yahoo endpoint — see BenchmarkOptions' remarks.
$APP download
$APP download --symbols RELIANCE,TCS --years 3           # or a subset
# (`fetch --symbol S --from D --to D` still exists for one-off single-symbol caching.)

# 2. See what the scanner ranks today, against the cache you just built
$APP scan

# 3. Backtest the strategy over that cached history
$APP backtest --from 2022-01-01 --to 2026-08-01

# 4. Stress-test that backtest's trade sequence
$APP montecarlo --run-id 1 --iterations 2000

# 5. Rolling out-of-sample validation
$APP walkforward --from 2021-01-01 --to 2026-08-01

# 6. Parameter sweep (default: RiskPercentPerTrade x StopAtrMultiplierMin; `optimize --list-params` shows the rest).
#    Every candidate is scored on Train and Validate, ranked by robustness, anti-overfitting-checked, and stored in OptimizationRuns.
$APP optimize --from 2021-01-01 --to 2026-08-01 --param BreakoutMinAdx=15,20,25 --param RiskPercentPerTrade=0.75,1,1.25

# 7. Run one paper-trading cycle (always simulated — see TradeCycleCommand)
$APP paper

# 8. Check where things stand
$APP portfolio

# 9. Start the REST API + dashboard (GET /api/portfolio /positions /orders
#    /signals /scanner /risk /performance /backtests /metrics /instruments
#    /chart/{symbol} /backtests/{id}/detail — open the same address in a
#    browser for the dashboard itself)
$APP serve                    # dashboard + API (add --scheduler: market-hours scanner + pre/post-market jobs)
$APP killswitch status        # also: activate --reason "..." | reset
```

`live` uses the exact same flow as `paper` but additionally requires
`LiveTrading:Mode=Live` AND `LiveTrading:AllowLiveTrading=true` in
config — either flag missing falls back to a paper-simulated cycle
automatically (`LiveModeGate`). `reconcile` compares local state against
the broker and is a no-op-but-honest in Paper mode, since there's no
external broker to compare against.

Secrets (Zerodha API key/secret/access token) are read from environment
variables, never from `config/appsettings.json`. Copy `.env.example` to
`.env` and fill in real values before enabling paper/live modes; `.env`
is git-ignored.

## Solution layout

```
TradingPlatform.sln
├── src/
│   ├── TradingPlatform.Core         # domain models, enums, interfaces, Options — no dependencies
│   ├── TradingPlatform.Data         # SQLite persistence (13 repositories) + Yahoo Finance provider
│   ├── TradingPlatform.Indicators   # hand-rolled technical indicators — depends only on Core
│   ├── TradingPlatform.Strategy     # regime, VCP, RS, scoring, breakout, staged entry, StrategyStepEngine, exit engine
│   ├── TradingPlatform.Risk         # position sizing, trailing stops, drawdown/loss-limit engines, R-multiple analytics
│   ├── TradingPlatform.Backtesting  # portfolio/execution simulators, BacktestEngine, walk-forward, Monte Carlo, grid search
│   ├── TradingPlatform.Execution    # order validation/management, kill switch, reconciliation, Zerodha adapter, live-cycle orchestration
│   ├── TradingPlatform.Scanner      # universe scanner, ranking, scheduled scanning
│   ├── TradingPlatform.Reporting    # CSV/JSON/HTML report export for all 7 spec-named report types
│   ├── TradingPlatform.Api          # 8-route REST surface (GET-only)
│   └── TradingPlatform.Application  # composition root: CLI (12 commands), DI, configuration, logging (Program.cs)
├── tests/
│   └── TradingPlatform.Tests        # hand-rolled [Fact]/Assert/TestRunner — ~630 tests, see docs/gap-log.md
├── config/appsettings.json          # every configurable threshold in the spec — no magic numbers in code
├── data/ logs/ reports/ backtests/ exports/   # runtime output directories (git-ignored, .gitkeep'd)
├── .github/workflows/ci.yml         # build + test on push/PR — written but never executed here, see docs/gap-log.md
└── docs/
    ├── gap-log.md                   # environment disclosure (spec §0.2), resolved ambiguities, bugs caught — read this first
    └── roadmap.md                   # tranche-by-tranche plan and status
```

`Core` and `Indicators` intentionally have zero framework dependencies
beyond the BCL. Every other project references `Microsoft.AspNetCore.App`
via `<FrameworkReference>` to get
`Microsoft.Extensions.{Hosting,DependencyInjection,Logging,Configuration,
Options,Http}` **without any NuGet restore** — see `docs/gap-log.md` for
why that matters here and how it works.

## Dashboard

`dotnet run -- serve` also serves a single-page dashboard
(`src/TradingPlatform.Application/wwwroot/index.html`) at the same
address — open it in a browser once `serve` is running. Material Design 3 styling (Inter Tight / Manrope, light + dark, needs internet for Google Fonts and ECharts CDN). Sections: Overview,
Positions, Orders, Signals, Scanner, **Chart & Indicators** (a
TradingView-influenced candlestick chart — Apache ECharts, linked
crosshair and synced zoom/pan across every pane — with SMA/HMA/Supertrend
overlays and individually-toggleable Volume/RSI/ADX/MACD/ATR panes for
any cached symbol, alongside its VCP pattern and relative-strength
context), Backtests (run list → equity curve/drawdown/yearly-returns/
trade-journal detail), and Risk & Performance. Every chart on the
dashboard is Apache ECharts (pinned CDN version, no bundler). It's a pure
static file talking to the REST API below — no build step, same "zero
NuGet, zero external tooling" philosophy as the rest of this repo.

## CLI reference

Run `dotnet run --project src/TradingPlatform.Application -- --help` for
the authoritative, current list. Every command below reads local SQLite
only — none of them call Yahoo or Kite directly except `fetch` and (when
credentials are configured) `reconcile`/`live`.

| Command | What it does |
|---|---|
| `env` | Live dependency-reachability + active-substitution disclosure |
| `universe import\|list\|show\|stats\|activate\|deactivate\|classify\|surveillance` | Manage the stock universe, classification and surveillance in SQLite (`import` is kept as an alias of `universe import`) |
| `download [--symbols --years --from --to --force]` | Download daily OHLC for the whole universe into the SQLite cache that every other command reads (incremental, resumable, failure-reporting) |
| `fetch --symbol S --from D --to D` | Cache historical bars for one symbol from Yahoo |
| `scan` | Rank the cached universe, persist the cycle, print top candidates |
| `backtest --from D --to D` | Run BacktestEngine, persist run/trades/snapshots, print the §8.1 report |
| `optimize --from D --to D [--param Name=v1,v2,..]... [--list-params --max-combinations N --top N]` | Grid search over the spec §5.5 parameters in the catalog (default: RiskPercentPerTrade x StopAtrMultiplierMin). Train+Validate scored, neighbour-averaged for robustness, §5.7-checked; candidates persisted to `OptimizationRuns`. Exit 2 = every candidate rejected |
| `walkforward --from D --to D [--train-years --validate-months --test-months --step-months]` | Rolling out-of-sample windows, plus chained out-of-sample CAGR / max drawdown and the §5.7 verdict. Exit 2 = rejected by the anti-overfitting check |
| `montecarlo --run-id N [--iterations --seed --mode reshuffle\|bootstrap\|both]` | Simulate an existing backtest run's trades: `reshuffle` = path risk (drawdown/ruin), `bootstrap` = resampled outcomes with a real ending-capital spread |
| `paper [--no-realtime]` | Run one paper-trading cycle (always simulated); while the NSE is open it prices off a live-quote overlay (Kite if credentials are set, else Yahoo) |
| `live [--no-realtime]` | Run one live-trading cycle (requires explicit config opt-in, else falls back to paper); real-time as above |
| `portfolio` | Print current NAV/positions/drawdown-tier summary |
| `reconcile` | Compare local state against the broker |
| `download-intraday --timeframe 60m\|15m\|5m [--days N --symbols A,B --to D --include-today --max-failures N]` | Cache intraday bars for the multi-timeframe layers (Yahoo serves ~59 days of 5m/15m and ~729 of 60m) |
| `import-delivery (--file F \| --dir D) [--all-symbols --dry-run]` | Load NSE delivery % (`sec_bhavdata_full` or generic CSV `Symbol,TradeDate,DeliveryPercent`) into the cache. Exit 0 clean / 2 some rows rejected / 1 nothing imported |
| `zerodha-login [--request-token T]` | Daily Kite session: prints the login URL, or exchanges a `request_token` for the access token (printed, never stored) |
| `serve` | Start the REST API (Kestrel, `ASPNETCORE_URLS` for the bind address) |

## How the market regime is computed

`RegimeScore` (0-100) is a weighted average of: broad-index trend (Nifty 500 vs 50/200 DMA and slope), **universe breadth**
(% of the universe above its 50 and 200 DMA), **advance/decline** (smoothed ratio of advancers to decliners that session),
small/mid relative strength, volatility, and the **breakout success rate** (share of trades closed in the last 180 days that made money).
Breadth, advance/decline and breakout success come from the platform's own cache and trade journal (`UniverseBreadthCalculator`,
`BreakoutSuccessRateCalculator`) — point-in-time in the backtest, from the full universe in `scan`/`paper`/`live` — and are persisted
per day in `MarketRegimes`. A component that cannot be computed honestly (fewer than `MarketRegime:BreadthMinimumSampleSize` symbols
that day, fewer than `BreakoutSuccessMinimumTrades` closed trades) is left out and its weight redistributed; it is never assumed good.

**Fails closed.** If the broad index has too little history for the 200-DMA trend (`FailClosedWithoutTrend`, default true), or no
component is available at all, the regime is score 0 / Emergency / **0% deployable** — volatility alone must not unlock deployment.
Backtests run before this change used a regime without breadth and with that fallback, so their numbers are not comparable.

## Multi-timeframe (optional, spec §3.8 / §7.3)

- **Weekly = market bias.** The broad index's daily bars are resampled to weekly; three signals (weekly close above the 10-week SMA, 10-week above 30-week SMA, 10-week SMA rising vs 4 weeks ago) give Bullish / Neutral / Bearish, or Unknown with too little history. It is stored with each regime snapshot and shown by `scan`. It does not change exposure unless you set `MarketRegime:WeeklyBearishDeployableCapPercent` (default off; only ever lowers exposure).
- **Daily = stock setup** (unchanged). **60m = entry refinement, 15m = execution** — *backtest only*: set `Backtest:SignalToOrderLag` to `Refinement60m` or `Refinement15m`, run `download-intraday --timeframe 60m` (and `15m`), then `backtest`. The daily signal is still the decision; the next session's entry executes only after an hourly bar closes above the breakout level (optionally on >= `RefinementMinTriggerRelativeVolume` x the average hourly volume of the previous sessions), at the next hourly open (or the first 15m open after the confirming hour). If no hourly bar confirms, the order is cancelled. Sessions with no cached intraday bars use the daily open (or are skipped with `RefinementSkipWhenNoIntradayData`); the report counts every outcome and warns when most entries were unrefined — with Yahoo's retention a multi-year backtest will mostly be unrefined.
- `paper` / `live` execute at the next open and **refuse to start** if `Strategy:SignalToOrderLag` is a refinement mode (there is no live 60m/15m feed). 5m bars can be downloaded and resampled but have no strategy role in the spec.

## Delivery % (optional, spec §3.4)

NSE publishes delivery statistics after the close. Download the daily `sec_bhavdata_full_DDMMYYYY.csv` files yourself and load them with `import-delivery` (only EQ-series rows for your active universe are kept by default). Two opt-in uses, both **off** by default: `StockScoring:UseDeliveryPercent` blends today's delivery % relative to the stock's own 20-session average into the Volume Accumulation score (no effect for a symbol/day without data), and `Strategy:BreakoutMinDeliveryRatio` adds a breakout condition requiring that ratio — **a missing figure fails the gate**, so enable it only once the whole universe is being imported. While a session is still forming, scans use the last completed session's delivery.

## Entry sizing and gap risk

Position size is computed at plan time from the planned entry (the VCP pivot) and the stop derived from it. A market order fills at the next
open, which for a breakout is often above the pivot — leaving the stop much further below the real fill than the budget assumed. With
`Risk:ResizeEntryForGapRisk` (default true) the entry is scaled down so rupee risk stays at the planned budget (never up), cancelled if the
price is at/through the stop, and the backtest records each position's risk — and therefore its R-multiples and R-based trailing/pyramid
triggers — from the ACTUAL fill. `paper`/`live` apply the same sizing guard (a `GAP` line is printed). Set it to false to reproduce the older behaviour
exactly. **Backtests from builds before this change are not comparable** (they took more risk than budgeted and flattered R). The spec has no
"too extended" rule, so a stock far above its pivot is sized down, not skipped.

## Optimization and overfitting checks

`optimize` sweeps the parameters listed by `optimize --list-params` — risk %, stop ATR multiples, portfolio heat, ADX floor, breakout
volume multiplier, RS breakout threshold, HMA periods, trailing ATR multiple / trigger, momentum-failure RS, regime thresholds. RSI and
VCP-score thresholds are **not** sweepable because the breakout gate has no such condition (a sweep would change nothing). Combinations
that break an invariant (fast HMA >= slow HMA, unordered regime thresholds, ...) are skipped and counted, never silently repaired.

Ranking: `Validate score - max(0, Train score - Validate score)`, then averaged 50/50 with the scores of adjacent grid cells so a lone spike
loses to a plateau. A candidate is **rejected** (never the winner) if max drawdown exceeds `Backtest:MaxAcceptableDrawdownPercent`
(40%) or Train was positive but Validate was not (spec §5.7). Other flags (too few trades, single stock / year / regime dependence,
survivorship bias) are shown as warnings. The Validate slice is what the winner was *chosen* on, so it is not clean out-of-sample:
apply the winner in config and run `walkforward`.

## Zerodha setup and activating live trading

1. Create a Kite Connect app; put `ZERODHA_API_KEY` and `ZERODHA_API_SECRET` in `.env` (never in `appsettings.json`).
2. Each trading day: `zerodha-login` -> open the URL -> copy `request_token` from the redirect -> `zerodha-login --request-token ...`
   -> export the printed `ZERODHA_ACCESS_TOKEN`. Tokens expire daily.
3. Run `paper` for weeks first. `live` only places real orders when **both** `LiveTrading:Mode=Live` and `LiveTrading:AllowLiveTrading=true`
   (the Development overlay can never set the latter). Anything else is simulated.
4. The Zerodha adapter has never been exercised against the real service (`ZerodhaOptions:AdapterVerifiedAgainstLiveDocs=false`).
   Do a single tiny live order and check `reconcile` before trusting it with capital. `killswitch activate --reason ...` halts new orders; a latched switch needs `killswitch reset`.

## Performance expectations

Measured in the build sandbox (one CPU) on a synthetic universe: ~26 s for 150 symbols x 1300 sessions, i.e. roughly 0.2 ms per symbol-day. A 500-symbol, 5-year `backtest` is therefore on the order of a couple of minutes; `optimize` runs two backtests per combination, so keep `--max-combinations` modest (the default 2-parameter grid is 12 combinations). Your hardware and real data (which produce trades, unlike the benchmark) will differ.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `scan` ranks nothing, with `Regime: Emergency (score 0 ...)` | The broad index (`^CRSLDX`) has under 200 cached sessions, so the regime fails closed. `download` more history (>= 1 year). |
| `No cached bars for the broad-index benchmark` | Run `download` (or `fetch --symbol ^CRSLDX ...`) first. |
| Everything excluded as Unknown / no surveillance | Classification and surveillance must be loaded (`universe import ...`, `universe classify --auto`, `universe surveillance ...`); see `docs/data-import.md`. |
| `optimize` exits 2 | Every candidate was rejected by the §5.7 check — do not adopt any of them. |
| `walkforward` exits 2 | The chained out-of-sample result fails the §5.7 check (drawdown above the ceiling, or Train positive / Test not). |
| `database is locked` | Another process holds a write transaction for more than 5 s. The DB runs in WAL mode, so readers (`serve`) do not block; stop the other writer. |
| `zerodha-login` token exchange fails | `request_token` is single-use and short-lived; fetch a new one. Check the API key/secret variables. |
| `Interrupt received...` | Ctrl+C asks the command to stop at its next safe point; press again to force-quit (not recommended during `live`). |

## Security

- Secrets only via environment variables (`.env`, git-ignored). The access token is printed by `zerodha-login`, never stored.
- **REST API / dashboard access control.** Set `TRADING_API_KEY` and every `/api/*` request must send it in an `X-Api-Key` header (the
  dashboard asks for it once per tab and keeps it in `sessionStorage`; the static page itself carries no account data). If `serve` would listen
  on anything but loopback (`ASPNETCORE_URLS`, e.g. the Docker image's `0.0.0.0`) and no key is set, it **refuses to start** unless you pass
  `--allow-unauthenticated`. The key travels in clear over plain HTTP: for anything beyond localhost use a TLS reverse proxy or an SSH tunnel.
  With no key and a loopback bind (the default) the API stays open, as before.
- The database holds your positions and orders: restrict file permissions on `data/`. `DailyJobs` writes verified daily snapshots
  (`VACUUM INTO`, integrity-checked) under `data/backups/`.

## Known data limitations

- Yahoo Finance is an unofficial source: occasional gaps, late corrections and rate limiting. Corporate-action detection flags suspicious
  jumps; it does not re-adjust history. The Yahoo and Zerodha adapters have never been run against the real services from the build sandbox.
- No point-in-time index constituents: **every** backtest is currently flagged `SURVIVORSHIP-BIASED` (the flag is always set, because the universe is today's list) — treat all backtest results as optimistic.
- Market cap, sector and surveillance (ASM/GSM) data must be imported; the sample `universe.csv` carries none of them.
- Intraday history is short (Yahoo: ~59 days of 5m/15m, ~729 of 60m); delivery % is never downloaded automatically, and the NSE file layout the importer expects has not been tested against a real download.
- Intraday volume is projected from the elapsed session fraction (a heuristic); NSE holidays are not modelled by the scheduler.

## Production deployment

Run `serve --scheduler` under a process supervisor (systemd / container restart policy) on one machine with the DB on local disk (not a
network share: WAL needs shared memory). Set `DOTNET_ENVIRONMENT=Production`, load `.env`, refresh the Kite token before 09:00 IST, keep
`data/backups/` off-box, and review `logs/` and the `SystemEvents` / `RiskEvents` tables daily. The shipped `ci.yml` and `Dockerfile`
have not been run in this sandbox.

## Design principles carried through every tranche

- **Provider-agnostic seams.** `IHistoricalMarketDataProvider`,
  `IExecutionProvider`, `IMarketRegimeService`, etc. live in `Core`; only
  the composition root (`Application/Program.cs`) knows which concrete
  implementation is wired in for Backtest vs. Scan vs. Paper vs. Live.
- **No silent defaults on missing data.** Sector, market-cap bucket,
  surveillance status, and fundamentals all use an explicit `Unknown` /
  `DataAvailability.Unavailable` state — never a value that happens to
  make a downstream check pass. Absence of a data pipeline (fundamentals,
  institutional activity, sector-benchmark bars — none of which have an
  ingestion source in this codebase) is treated identically: the
  consuming engine scores the input as unavailable, never fabricates one.
- **No magic numbers.** Every threshold lives in a strongly-typed
  `Options` class bound from `config/appsettings.json`.
- **Reproducibility.** Every signal/trade carries `StrategyVersion` and
  `ConfigurationVersion`; every backtest run persists a full config
  snapshot (including a SHA-256 hash of the resolved options) at start,
  before the engine produces a single result.
- **Two independent gates before any real order.** `dotnet run -- paper`
  can never place a real order no matter what's in config; `live` still
  additionally requires `Mode=Live AND AllowLiveTrading=true`
  (`LiveModeGate`) or it silently, safely falls back to a paper-simulated
  cycle.
- **Every simplification is a stated one.** Where this codebase falls
  short of the full spec — no fundamentals/institutional-activity/
  sector-benchmark data source, no continuous intraday daemon (every
  trading command is one cycle per invocation), no rolling
  rejection/slippage window for two of the five live kill-switch
  triggers — `docs/gap-log.md` says so explicitly, at the tranche that
  introduced it, with the reasoning for why.
