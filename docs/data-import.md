# Universe, data download and real-time data

Everything about *which stocks you trade* lives in the SQLite database and is managed by **one command family, `universe`**.
OHLC history is filled by a **separate `download` command**. Every other command (scan, backtest, optimize, walkforward,
montecarlo, paper, live) reads from that database — only `download`, `fetch` and the real-time quote providers talk to Yahoo/Kite.

```
universe.csv ──► universe import ──► Instruments ┐
marketcap.csv ─► universe import --kind marketcap │
                 universe classify  ──► classification (Large/Mid/Small/Micro) ├─► SQLite ─► scan · backtest · optimize
ASM/GSM lists ─► universe surveillance ─► surveillance status                  │          walkforward · montecarlo
                 download ──► daily OHLC (+ derived turnover)                  ┘          paper · live (+ live quotes)
```

## 1. `universe` — manage the stock universe

| action | what it does |
|---|---|
| `universe import --file universe.csv [--on-duplicate first\|last\|error] [--deactivate-missing] [--dry-run]` | Load/refresh stock details. |
| `universe import --kind marketcap --file f.csv` | `Symbol,MarketCap[,AverageDailyTurnover]` — updates **only** those columns. |
| `universe import --kind classification\|surveillance\|fundamentals\|institutional --file f.csv` | Bulk loaders (formats below). |
| `universe list [--bucket B] [--inactive\|--all] [--search text] [--limit N]` | Browse (search matches symbol, company, industry, sector). |
| `universe show --symbol X` | Details, classification history, surveillance, bar coverage. |
| `universe stats` | Counts, how many symbols are unclassified / lack surveillance / lack bars, last download. |
| `universe activate\|deactivate --symbol X` or `--symbols A,B` | Include/exclude from trading (data is kept). |
| `universe classify --symbol X --bucket Large\|Mid\|Small\|Micro [--rank N] [--sector S] [--from date]` | One symbol. |
| `universe classify --file classification.csv` | Many symbols, point-in-time. |
| `universe classify --auto --large-min N --mid-min N --small-min N [--from date]` | Derive buckets from `MarketCap` (**₹ crore**). The cut-offs are **required, with no defaults**: AMFI revises them semi-annually, and ranking inside a partial universe would label everything "Large". Below `--small-min` = Micro. Symbols with no market cap stay Unknown. |
| `universe surveillance --symbol X --flags ASM\|GSM\|TradeToTrade\|…\|none [--as-of date]` | One symbol. |
| `universe surveillance --asm-file f [--gsm-file f] [--t2t-file f] [--as-of date] [--assume-unlisted-clear]` | From list files (a CSV with a `Symbol` column, or one symbol per line). **Unlisted symbols stay Unknown unless you pass `--assume-unlisted-clear`** — that flag is your assertion that the lists are complete. |
| `universe surveillance --clear-all [--as-of date]` | Explicitly mark every active symbol clear (cannot be combined with list files). |

`import` still works as an alias of `universe import`.

### Rules that apply to every import
* **All-or-nothing.** Every row is validated first; if any row is bad *nothing* is written and every error is listed with its line number. `--dry-run` validates only.
* **Never fabricated.** Blank optional cells stay null/Unknown. For `MarketCap` and `AverageDailyTurnover`, **`0` means "unknown"** (it is how exporters write it) and is stored as null. `NA` for sector/industry is stored as null. A blank growth figure is *Unavailable*, never 0.
* **Duplicates are never silent.** A repeated symbol keeps the first row by default (`--on-duplicate last` keeps the later one, `error` rejects the file); each dropped row is reported with exactly which columns differed. Different symbols sharing an ISIN are reported too.
* **Idempotent.** Re-importing updates in place.
* Dates are `yyyy-MM-dd`; numbers use `.`; quoted CSV fields may contain commas. Column names are case-insensitive.

### CSV formats
| kind | required | optional |
|---|---|---|
| `universe` | `Symbol`, `CompanyName` | `NseSymbol`, `YahooSymbol`, `Isin`, `Sector`, `Industry`, `MarketCap`, `AverageDailyTurnover`, `LotSize`, `Exchange`, `Active` (defaults: `NseSymbol`=Symbol, `YahooSymbol`=Symbol+`.NS`, `Exchange`=NSE, `Active`=true) |
| `marketcap` | `Symbol`, `MarketCap` (> 0) | `AverageDailyTurnover` |
| `classification` | `Symbol`, `MarketCapBucket`, `EffectiveFrom` | `MarketCapRank`, `Sector`, `EffectiveTo` |
| `surveillance` | `Symbol`, `AsOfDate` | `Flags` (`ASM`, `GSM`, `TradeToTrade`, `SurveillanceFlag`, `CorporateActionRisk`, `LiquidityRisk`, `DataQualityRisk`; separated by `\|` or `;`; blank = checked and clear), `Source` |
| `fundamentals` | `Symbol`, `AsOfDate` | `EarningsGrowthPercent`, `RevenueGrowthPercent` |
| `institutional` | `Symbol`, `AsOfDate` | `ActivityScore` |

Notes on your `universe.csv`: `MarketCap` and `AverageDailyTurnover` are 0 for every row, so on import they become *unknown*.
Turnover is then **derived from the downloaded bars** (below). `MarketCap` has no source in the file — supply it with
`universe import --kind marketcap` to enable `classify --auto`; until then buckets are Unknown. `LotSize` holds values like 1/2/5/10
that look like share *face values*; the platform does not use it (NSE cash equities trade in lots of 1).

### Units that matter
`AverageDailyTurnover` is **₹ per day** (the scanner needs ≥ `Scanner:MinimumAverageDailyTurnover` = ₹1,00,00,000). `MarketCap` for
`classify --auto` is **₹ crore**.

### How current-state data is looked up
Classification, surveillance, fundamentals and institutional activity describe a stock **as of today** (the market's date, IST) for
`scan` / `paper` / `live` — not as of the last bar's date — so what you recorded this morning is used this morning. Surveillance may be up
to `Scanner:SurveillanceMaxStalenessDays` (default 3) days old, so Friday's list covers Monday; anything older is **Unknown, which is
rejected**. Backtests keep strict point-in-time lookups.

### Why a scan can come back empty
A symbol is dropped before ranking if it fails a universe gate: too few cached bars, price below the minimum, turnover below the minimum
(both the Instruments column *and* the value computed from bars), or **no surveillance status** (Unknown is never treated as safe).
`scan` prints a per-gate summary whenever nothing ranks; `scan --explain` lists every rejection.

## 2. `download` — OHLC for the whole universe

```bash
dotnet run -- download                         # every active symbol + the benchmark indices
dotnet run -- download --symbols RELIANCE,TCS  # a subset
dotnet run -- download --years 8               # deeper first-time history (default 5)
dotnet run -- download --force                 # re-fetch the last --refresh-days even if cached
```
* **Incremental and resumable.** Only the missing tail (plus a 5-day overlap that replaces recently revised bars) is fetched; Ctrl+C is safe — re-run to continue. A second run on the same day makes no requests.
* **Benchmarks first.** Their last bar defines the market's last session, so a holiday does not make every stock look "behind".
* **Never stores a forming bar.** While today's session is open or not yet settled (close + 20 min), today's bar is fetched but **not stored** (`--include-today` overrides). Intraday prices come from the real-time overlay instead.
* **Reports every failure** (per symbol, with the reason) and **aborts after `--max-failures` consecutive failures** (default 10) instead of grinding through 300 retries when the network is down. Exit code: 0 clean, 2 failures/aborted, 130 cancelled.
* **Derives `AverageDailyTurnover`** per symbol from the stored bars (mean close × volume over `Scanner:LiquidityLookbackDays`); `--keep-turnover` opts out.
* Pace: Yahoo is rate-limited to 60 requests/minute by `YahooFinance:RateLimitRequestsPerMinute`, so ~300 symbols take ≈ 5 minutes.
* The pre-market job (`serve --scheduler`) uses this same downloader.

## 3. Real-time data for `paper` and `live`

While the NSE session is open, daily bars alone are a day stale. So `paper`/`live` (config `Realtime:*`) take a **live quote** and lay today's
forming bar over the cached history, so indicators, VCP/breakout detection, stops and paper fills all see the current price:

1. **Exits/stops**: open positions are quoted first; stops are evaluated against today's live low/last price.
2. **Entries**: the daily scan shortlists candidates, **only the shortlist is quoted**, and it is **re-ranked on today's price and volume**. Shortlisted symbols without a valid quote are excluded from entries.
3. **Paper fills** use the live price (not yesterday's open); capacity is the larger of today's volume so far and the last full session's.
4. **Volume** so far is projected to a full-day estimate only once ≥ 15 % of the session has elapsed; before that raw volume is used, which makes breakout confirmation *harder*, never easier.

**Source:** `Realtime:Provider` = `Auto` (Kite when `ZERODHA_*` credentials are set, else Yahoo), `Kite`, or `Yahoo`. An explicit `Kite` without credentials does **not** silently fall back.

**A quote is rejected, never trusted, when it is** older than `MaxQuoteAgeSeconds` by the *source's own timestamp* (this is what catches a delayed feed), from another session, more than 25 % from the previous close, or its previous close disagrees with the cached last close by > 3 % (the cache is behind — run `download` — or a split/bonus happened). A symbol with no valid quote is evaluated on daily bars only and **excluded from new entries**. If *no* quote is usable the cycle says so and sends a SystemFailure notification; `--no-realtime` forces plain daily-bar mode.

Outside market hours nothing changes (final daily bars are used). Flags: `--no-realtime`; `--realtime-assume-open` (testing / dry-runs: treat the market as open).
The regime and benchmark data stay on daily bars; the scheduled scanner (`serve --scheduler`) and `scan` are daily-bar based.

## 4. Scheduled jobs (`serve --scheduler`)
* **Pre-market** (`Scanner:PreMarketLeadMinutes` before the open, weekdays): `download` refresh, universe validation, regime calc, reconciliation.
* **Intraday** (every `Scanner:IntervalMinutes` while open): the scanner.
* **Post-market** (`Scanner:PostMarketDelayMinutes` after the close): reconciliation, performance summary, integrity-checked SQLite snapshot to `data/backups/`, daily JSON report to `reports/`.

NSE holidays are not modelled; a holiday run finds nothing new and is harmless. Reconciliation only does real work in `Mode=Live`.
