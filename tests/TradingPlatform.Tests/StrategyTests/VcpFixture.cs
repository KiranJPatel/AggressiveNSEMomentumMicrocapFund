using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>
/// A hand-designed 36-bar synthetic OHLCV series forming a textbook 3-leg
/// VCP: prior uptrend (bars 0-11, 60 -> 98.5), then contractions of
/// depth ~21.4% (H0=105@12 -> L0=82.5@16), ~11.5% (H1=99.0@20 -> L1=87.6@24),
/// and ~6.6% (H2=96.5@28 -> L2=90.175@31), each with a higher low than the
/// last, plus a volume profile that steadily declines through the base with
/// a pronounced dry-up on the final leg (avg ~51,250 vs a base average of
/// ~118,750 — a ~57% drop). Swing detection (lookback=3) and every
/// contraction depth was verified against an independent Python
/// reimplementation before being transcribed here (see docs/gap-log.md
/// methodology note for Tranche 4), the same cross-validation approach
/// used for the Tranche 3 indicator fixtures.
/// </summary>
public static class VcpFixture
{
    public const string Symbol = "VCPTEST";

    // (High, Low, Open, Close, Volume) — Open/Close both set to the bar's
    // midpoint since VcpDetector only reads High/Low/Volume/Timestamp.
    private static readonly (decimal High, decimal Low, decimal Open, decimal Close, long Volume)[] GoldenCase =
    {
        (60.0m, 58.5m, 59.25m, 59.25m, 150000L),
        (63.5m, 62.0m, 62.75m, 62.75m, 145000L),
        (67.0m, 65.5m, 66.25m, 66.25m, 155000L),
        (70.5m, 69.0m, 69.75m, 69.75m, 148000L),
        (74.0m, 72.5m, 73.25m, 73.25m, 152000L),
        (77.5m, 76.0m, 76.75m, 76.75m, 149000L),
        (81.0m, 79.5m, 80.25m, 80.25m, 151000L),
        (84.5m, 83.0m, 83.75m, 83.75m, 147000L),
        (88.0m, 86.5m, 87.25m, 87.25m, 153000L),
        (91.5m, 90.0m, 90.75m, 90.75m, 150000L),
        (95.0m, 93.5m, 94.25m, 94.25m, 146000L),
        (98.5m, 97.0m, 97.75m, 97.75m, 152000L),
        (105m, 103.5m, 104.25m, 104.25m, 220000L),   // H0 (idx 12)
        (99.75m, 98.25m, 99.0m, 99.0m, 210000L),
        (94.5m, 93.0m, 93.75m, 93.75m, 190000L),
        (89.25m, 87.75m, 88.5m, 88.5m, 175000L),
        (84.0m, 82.5m, 83.25m, 83.25m, 160000L),      // L0 (idx 16)
        (87.75m, 86.25m, 87.0m, 87.0m, 145000L),
        (91.5m, 90.0m, 90.75m, 90.75m, 140000L),
        (95.25m, 93.75m, 94.5m, 94.5m, 135000L),
        (99.0m, 97.5m, 98.25m, 98.25m, 130000L),      // H1 (idx 20)
        (96.525m, 95.025m, 95.775m, 95.775m, 120000L),
        (94.05m, 92.55m, 93.3m, 93.3m, 110000L),
        (91.575m, 90.075m, 90.825m, 90.825m, 100000L),
        (89.1m, 87.6m, 88.35m, 88.35m, 95000L),        // L1 (idx 24)
        (90.95m, 89.45m, 90.2m, 90.2m, 85000L),
        (92.8m, 91.3m, 92.05m, 92.05m, 80000L),
        (94.65m, 93.15m, 93.9m, 93.9m, 75000L),
        (96.5m, 95.0m, 95.75m, 95.75m, 70000L),        // H2 (idx 28)
        (94.8917m, 93.3917m, 94.142m, 94.142m, 55000L),
        (93.2833m, 91.7833m, 92.533m, 92.533m, 45000L),
        (91.675m, 90.175m, 90.925m, 90.925m, 35000L),  // L2 (idx 31)
        (92.7562m, 91.2562m, 92.006m, 92.006m, 60000L),
        (93.8375m, 92.3375m, 93.088m, 93.088m, 65000L),
        (94.9188m, 93.4188m, 94.169m, 94.169m, 70000L),
        (96.0m, 94.5m, 95.25m, 95.25m, 75000L),
    };

    public const int H0Index = 12, L0Index = 16, H1Index = 20, L1Index = 24, H2Index = 28, L2Index = 31;
    public const decimal Depth1 = 21.428571428571427m, Depth2 = 11.515151515151521m, Depth3 = 6.554404145077723m;
    public const decimal ExpectedVcpScore = 57.185371m;

    public static IReadOnlyList<HistoricalBar> BuildGoldenCase() => Build(GoldenCase);

    /// <summary>Same shape as the golden case, but with the prior uptrend flattened into a decline — the uptrend gate should reject this.</summary>
    public static IReadOnlyList<HistoricalBar> BuildWithoutPriorUptrend()
    {
        var bars = GoldenCase.ToArray();
        for (var i = 0; i < 12; i++)
        {
            var flat = 105m; // flat/declining lead-in ending at the same H0 level, no genuine rise
            bars[i] = (flat - i * 0.1m, flat - i * 0.1m - 1.5m, flat - i * 0.1m - 0.75m, flat - i * 0.1m - 0.75m, bars[i].Volume);
        }
        return Build(bars);
    }

    /// <summary>Truncated after the first contraction only — below MinContractions (default 2).</summary>
    public static IReadOnlyList<HistoricalBar> BuildSingleContractionOnly()
    {
        // H0..L0 plus enough trailing bars to confirm L0 as a swing low, nothing more.
        var bars = GoldenCase.Take(L0Index + 4).ToArray();
        return Build(bars);
    }

    /// <summary>Widening contractions (5% -> 10% -> 20%) — the opposite of a VCP; each leg should fail the tightening test and reset.</summary>
    public static IReadOnlyList<HistoricalBar> BuildWideningContractions()
    {
        var rows = new List<(decimal, decimal, decimal, decimal, long)>();
        void AddBar(decimal high, long volume)
        {
            var low = high - 1.5m;
            rows.Add((high, low, (high + low) / 2, (high + low) / 2, volume));
        }

        for (var i = 0; i < 12; i++) AddBar(60 + i * 3.5m, 150000L); // prior uptrend
        AddBar(100m, 200000L);                                       // H0
        foreach (var h in Leg(100m, 95m, 4)) AddBar(h, 180000L);      // L0: 5% deep
        foreach (var h in Leg(95m, 102m, 4)) AddBar(h, 150000L);      // recover, new high H1 > H0 (fine, unrelated to depth calc)
        foreach (var h in Leg(102m, 91.8m, 4)) AddBar(h, 170000L);    // L1: ~10% deep (wider than 5%) -> resets
        foreach (var h in Leg(91.8m, 100m, 4)) AddBar(h, 140000L);    // recover to H2
        foreach (var h in Leg(100m, 80m, 4)) AddBar(h, 200000L);      // L2: 20% deep (wider again) -> resets
        foreach (var h in Leg(80m, 88m, 4)) AddBar(h, 120000L);       // trailing confirmation bars

        return Build(rows.ToArray());
    }

    private static IEnumerable<decimal> Leg(decimal start, decimal end, int steps)
    {
        for (var i = 1; i <= steps; i++)
            yield return decimal.Round(start + (end - start) * i / steps, 4);
    }

    private static IReadOnlyList<HistoricalBar> Build(IReadOnlyList<(decimal High, decimal Low, decimal Open, decimal Close, long Volume)> rows)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        var bars = new List<HistoricalBar>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            bars.Add(new HistoricalBar
            {
                Symbol = Symbol,
                Timestamp = start.AddDays(i),
                Open = rows[i].Open,
                High = rows[i].High,
                Low = rows[i].Low,
                Close = rows[i].Close,
                AdjustedClose = rows[i].Close,
                Volume = rows[i].Volume,
                Source = "Fixture",
                Timeframe = Timeframe.Daily
            });
        }
        return bars;
    }
}
