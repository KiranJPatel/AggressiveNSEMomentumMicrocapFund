using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.StrategyTests;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>
/// A full entry-to-exit scenario for BacktestEngine's end-to-end test:
/// Phase 1 (36 bars) reuses VcpFixture's already-unit-tested golden-case
/// VCP base verbatim (pivot 96.5), re-tagged to this fixture's own symbol.
/// Phase 2 (35 bars) breaks out above the pivot on a volume spike and
/// climbs steadily (~2%/day compounding) — smooth and persistent enough
/// to clear BreakoutEngine's ADX/HMA/RS gates without any hand-tuning
/// beyond "a strong, sustained trend" (see BreakoutEngine's condition
/// list — RS clears via the stock's 20/50-day outperformance saturating
/// the tanh curve even though the flat-ish benchmark and short 90/180-day
/// windows read neutral, per RelativeStrengthCalculator's own documented
/// "not enough history -> neutral, not fabricated" behavior). Phase 3 (5
/// bars) gaps sharply down through any plausible stop level.
///
/// The benchmark (BuildFlatBenchmark) is a mild, steady drift with none of
/// the stock's volatility, so the stock's RS score comes out strongly
/// positive without needing to hand-tune the benchmark's shape at all.
/// </summary>
public static class BacktestEngineFixture
{
    public const string Symbol = "MOMENTUM1";
    public const decimal Pivot = 96.5m;

    public static IReadOnlyList<HistoricalBar> BuildStockBars()
    {
        var rows = new List<(decimal High, decimal Low, decimal Open, decimal Close, long Volume)>();

        // Phase 1: VcpFixture's golden-case VCP base (36 bars), values only.
        foreach (var bar in VcpFixture.BuildGoldenCase())
            rows.Add((bar.High, bar.Low, bar.Open, bar.Close, bar.Volume));

        // Phase 2: breakout (day 36) + steady ~2%/day runup for 34 more bars,
        // with periodic volume spikes (every 5th day) so RelativeVolume
        // clears 1.5x on SOME day after RS also clears 70 — a smooth
        // constant-volume runup alone converges its own 20-day average
        // volume to ~1.0x of itself and never re-clears the threshold
        // after the initial breakout-day spike.
        var close = 100m;
        rows.Add((101m, 98m, 99m, close, 250_000L)); // decisive break above the 96.5 pivot, volume spike
        for (var i = 0; i < 44; i++)
        {
            var open = close;
            close = Math.Round(open * 1.02m, 4);
            var high = Math.Round(close * 1.01m, 4);
            var low = Math.Round(open * 0.995m, 4);
            var volume = (i + 1) % 5 == 0 ? 260_000L : 90_000L;
            rows.Add((high, low, open, close, volume));
        }

        // Phase 3: sharp gap-down decline (5 bars) — plunges through any plausible stop.
        for (var i = 0; i < 5; i++)
        {
            var open = Math.Round(close * 0.85m, 4);
            var low = Math.Round(open * 0.95m, 4);
            var high = Math.Round(open * 1.01m, 4);
            close = Math.Round(open * 0.98m, 4);
            rows.Add((high, low, open, close, 180_000L));
        }

        return Build(Symbol, rows);
    }

    /// <summary>One bar for every date BuildStockBars() has, so the trading calendar (derived from this) never halts the stock — keeps this test focused on the entry/exit pipeline, not gap-handling (covered elsewhere).</summary>
    /// <summary>
    /// <paramref name="warmupBars"/> extra sessions BEFORE the stock's first bar: the regime engine now fails closed without a 200-session
    /// index trend (spec §0.4), so a benchmark exactly as short as the stock's base would — correctly — never allow an entry. The stock
    /// series itself is unchanged, so every existing entry/exit expectation still describes the same scenario.
    /// </summary>
    public static IReadOnlyList<HistoricalBar> BuildFlatBenchmark(int barCount, int warmupBars = 210)
    {
        var rows = new List<(decimal High, decimal Low, decimal Open, decimal Close, long Volume)>();
        var close = 1000m;
        for (var i = 0; i < barCount + warmupBars; i++)
        {
            var open = close;
            close = Math.Round(open * 1.0005m, 4); // ~13%/year mild drift, nowhere near the stock's climb
            rows.Add((close * 1.002m, open * 0.998m, open, close, 5_000_000L));
        }
        return Build("NIFTY500", rows, -warmupBars);
    }

    private static IReadOnlyList<HistoricalBar> Build(string symbol, IReadOnlyList<(decimal High, decimal Low, decimal Open, decimal Close, long Volume)> rows, int startOffsetDays = 0)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(startOffsetDays);
        var bars = new List<HistoricalBar>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            bars.Add(new HistoricalBar
            {
                Symbol = symbol,
                Timestamp = start.AddDays(i),
                Open = rows[i].Open,
                High = rows[i].High,
                Low = rows[i].Low,
                Close = rows[i].Close,
                AdjustedClose = rows[i].Close,
                Volume = rows[i].Volume,
                Source = "Fixture",
                Timeframe = Timeframe.Daily,
            });
        }
        return bars;
    }
}
