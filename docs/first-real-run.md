# First real run — what to expect, what to check, what to send back

Everything in this repository has been built and tested with fakes and synthetic data. **None of it has run against Yahoo, Kite or NSE
files, and no real strategy results exist yet.** This runbook is the order to find that out cheaply, with the money part last.
(The command list itself is in the README's "A first real run, end to end".)

## 0. Before anything

| Check | Healthy | If not |
|---|---|---|
| `dotnet --version` | 8.x | install the .NET 8 SDK |
| `env` | Yahoo reachable; substitutions listed | network/proxy problem — nothing below will work |
| Secrets | in `.env` / environment only, never in `appsettings.json` | see README > Security |

## 1. Small first, then the whole universe

1. `download --symbols RELIANCE,TCS --years 3` and confirm the benchmark `^CRSLDX` also downloads (it needs **>= 200 sessions** or the regime
   fails closed to 0% deployable). Look at the failure list at the end: a 429/timeout run aborts after consecutive failures and resumes next run.
2. Only then `download` for the full universe (long; resumable).
3. `universe stats` — unknown market cap / classification / surveillance means those symbols are excluded, by design.

## 2. Read the first `scan` carefully

`scan` prints a regime line, then ranked rows. `scan --explain` also prints why symbols were rejected, and price structure for ranked ones.

| You see | Meaning | Action |
|---|---|---|
| `Regime: Emergency (score 0 ... deployable 0%)` | index history < 200 sessions, or no regime component available | download more benchmark history |
| `>50DMA: n/a  A/D: n/a` | fewer than 20 symbols have a bar for that session (normal for a small test download) | download more of the universe |
| `weekly bias: Unknown` | < 34 weeks of index history | more history |
| nothing ranked, rejections all "Unknown" gates | classification / surveillance not loaded | `docs/data-import.md` |
| `breakout success: n/a` | fewer than 10 closed trades in the journal — expected at first | none |

## 3. Backtest, then distrust it

- `backtest --from ... --to ...`. **Every backtest is flagged SURVIVORSHIP-BIASED** (today's universe, no delisted names): treat results as optimistic.
- Look at: trade count (the report warns below 30), max drawdown vs the 40% ceiling (`[VERDICT] REJECTED`), worst year, recovery days,
  regime time split. Many data-quality flags in `SystemEvents` = fix the data before reading anything into the numbers.
- `montecarlo --run-id N --mode both`: `reshuffle` is path risk (drawdown/ruin), `bootstrap` is outcome spread.
- `walkforward`: the number that matters is the chained **out-of-sample** CAGR/drawdown and the verdict (exit 2 = rejected). It uses your
  config as-is — it does not re-optimize per window.
- Only then `optimize` with a **small** grid (`--max-combinations`; each combination = two backtests). Apply a winner to config and
  re-run `walkforward`; the Validate slice it was chosen on is not a clean test.

Reading the numbers: the report's `Entry execution` note says how far entries filled above the pivot and how many the gap-risk guard scaled down
(`Risk:ResizeEntryForGapRisk`, on by default). If an `optimize` sweep of `RiskPercentPerTrade` gives identical scores, that is normally the
position-size cap (`Portfolio:MaxSinglePositionPercent`) binding before risk does — not a broken sweep.

Expect the first results to disappoint or to look too good. The regime weights/thresholds have **not** been tuned on real data since breadth
and advance/decline were added, and the optional features (weekly-bias cap, intraday refinement, delivery %) are off by default for that reason.

## 4. Paper before anything else

`paper` is always simulated. Run it daily for weeks (`serve --scheduler` automates the daily jobs; protect the API with `TRADING_API_KEY`).
Compare its decisions with `scan --explain`. Do **not** touch `live` until paper behaves for a long stretch.

## 5. If you ever go live

Read README > "Zerodha setup and activating live trading". The broker adapter has never touched the real service: start with one tiny order,
then `reconcile`. `killswitch activate --reason "..."` halts new orders; Ctrl+C during a cycle finishes the cycle rather than leaving an order half-recorded.

## What to send back if something looks wrong

Please include (redact API keys, secrets and any access token — never paste those):

1. The exact command and its full console output (for `scan`, include `--explain`).
2. `env` output.
3. The newest file under the configured `Logs` directory, and the last ~50 rows of the `SystemEvents` table.
4. For a backtest/walk-forward question: the printed report and the run id.
5. What you expected instead.

Real output is far more useful than a description: every defect found so far in this project was found by running something, not by reading it.
