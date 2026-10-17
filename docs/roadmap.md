# Implementation Roadmap

Tranches map roughly to the master spec's Parts 1-8. Each tranche should
leave the solution building clean and the hand-rolled test suite green
before moving to the next. Strategy/config version tag: `AggressiveMomentumV1`.

| # | Tranche | Spec ref | Status |
|---|---|---|---|
| 0 | Solution structure, domain models, configuration, logging, environment disclosure | §0, §1.2, §7.3 (Options), §7.2 (logging) | **Done** |
| 1 | SQLite schema + hand-rolled P/Invoke data access + repositories + real `ISystemEventWriter` | §7.2 | **Done** |
| 2 | Yahoo Finance historical data provider & cache, retry/backoff, data quality & corporate-action detection | §5.2, §5.3 | **Done** |
| 3 | Technical indicators (HMA, Supertrend, SMA, RSI, MACD, ADX, ATR, HV, volume) — hand-rolled, unit-tested against published reference values | §3.4 | **Done** |
| 4 | VCP detection engine, unit-tested against synthetic contraction patterns | §3.5 | **Done** |
| 5 | Relative strength engine + market regime engine | §3.1, §3.3 | **Done** |
| 6 | Stock scoring engine (100-pt composite) + breakout engine | §3.2, §3.6 | **Done** |
| 7 | Staged entry model + shared `StrategyStepEngine` (Backtest/Paper/Live parity seam) | §3.7, §3.8, §5.6 | **Done** |
| 8 | Position sizing + portfolio heat + sector/correlation + microcap risk rules, with binding-constraint proof tests | §4.1, §4.2 | **Done** |
| 9 | Exit engine, trailing stop ladder, R-multiple analytics | §4.3, §4.4 | **Done** |
| 10 | Drawdown engine, circuit breakers, daily/weekly/monthly loss limits | §4.5 | **Done** |
| 11 | Backtesting engine: portfolio/execution simulators, slippage models, transaction costs, no-look-ahead enforcement | §5.1, §5.4 | **Done** |
| 12 | Execution: paper trading provider, Zerodha adapter (**UNVERIFIED** until manual doc-diff), order manager, position reconciliation | §6.2-§6.4 | **Done** |
| 13 | Walk-forward validation, parameter optimization (grid search), Monte Carlo simulation, anti-overfitting checks | §5.5, §5.7 | **Done** |
| 14 | Universe scanner, ranking engine, scheduled/hosted background service | §7.1 | **Done** |
| 15 | Live trading orchestration: modes, kill switch, restart recovery, full order-flow state machine | §6.1, §6.3-§6.5 | **Done** |
| 16 | Reporting & exports (CSV/JSON/HTML: Backtest/Trade/Portfolio/Risk/Scanner/Optimization/MonteCarlo reports) | §7.5, §8.1 | **Done** |
| 17 | Thin REST API surface for future dashboard consumption | §7.5 | **Done** |
| 18 | Nine deferred repositories (Signal/Order/Position/Trade/RiskEvent/DrawdownEvent/PortfolioSnapshot/ScannerResult/BacktestRun), real API query-service wiring, `portfolio`/`scan`/`backtest` CLI commands, opt-in backtest performance fix | §7.2, §7.3, §7.5, §8.1 | **Done** |
| 19 | `reconcile`/`montecarlo`/`walkforward`/`optimize` CLI commands (engines already built in Tranches 12/13, this was Application-layer wiring) | §5.5, §6.4, §7.3 | **Done** |
| 20 | `paper`/`live` CLI commands (signal generation -> sizing -> order validation -> submission -> fill/broker execution -> persistence), new EngineState persistence for DrawdownState/LossLimitState/KillSwitchState | §5.7, §6.1-§6.5, §7.3 | **Done** |
| 21 | Observability (`/api/metrics` + fetch/scan/backtest latency logging), CI config, optional Dockerfile, README pass, full line-by-line acceptance-criteria review against the master spec | §7.5, §8 | **Done** |

Every tranche should also revisit `docs/gap-log.md` — some "Deferred to
Tranche N" rows above are expected to resolve as that tranche is built;
others may reveal new NuGet-shaped gaps only discoverable once the code is
actually written.
