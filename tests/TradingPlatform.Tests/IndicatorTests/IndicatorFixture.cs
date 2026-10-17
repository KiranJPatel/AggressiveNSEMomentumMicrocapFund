using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Tests.IndicatorTests;

/// <summary>
/// Shared 40-bar synthetic OHLCV fixture and cross-language reference
/// values. The reference values were computed independently in Python
/// (stdlib only — math.log/math.sqrt, no external TA library) from the same
/// well-documented, publicly established formulas each indicator's own XML
/// doc cites (Wilder's RSI/ATR/ADX per StockCharts.com ChartSchool; HMA per
/// alanhull.com; Supertrend per TradingView's reference algorithm) — not
/// copied from any single textbook worked example. Two independent
/// implementations of the same public-domain formula agreeing to 3+ decimal
/// places is direct evidence the C# implementation is correct (spec §3.4:
/// "unit-tested against known reference values").
///
/// Only a handful of spot-check indices are asserted per indicator (14, 19,
/// 20, 25, 30, 35, 39) rather than the full 40-value series, to keep this
/// file readable — a wrong formula would miss at essentially every index,
/// so a sparse spot-check across the warm-up boundary and steady-state
/// region is sufficient.
/// </summary>
public static class IndicatorFixture
{
    public const string Symbol = "TESTSYM";
    public const decimal Tolerance = 0.001m;
    public const decimal LooseTolerance = 0.01m; // for HV / DI / ADX, which compound more division steps

    public static IReadOnlyList<HistoricalBar> Bars { get; } = BuildBars();

    private static IReadOnlyList<HistoricalBar> BuildBars()
    {
        // (Open, High, Low, Close, Volume) — see IndicatorFixture class remarks.
        (decimal Open, decimal High, decimal Low, decimal Close, long Volume)[] raw =
        {
            (99.53m, 101.10m, 99.31m, 100.82m, 124188L),
            (101.21m, 101.84m, 100.79m, 101.75m, 81788L),
            (101.76m, 101.79m, 101.11m, 101.31m, 118993L),
            (101.03m, 102.43m, 100.22m, 101.84m, 80390L),
            (102.04m, 103.50m, 101.88m, 103.16m, 137432L),
            (102.75m, 103.17m, 101.90m, 103.07m, 116223L),
            (103.30m, 104.93m, 102.33m, 104.39m, 102712L),
            (104.72m, 105.57m, 103.86m, 104.95m, 114641L),
            (104.50m, 106.19m, 104.21m, 105.96m, 84788L),
            (105.56m, 105.84m, 104.92m, 105.56m, 101889L),
            (105.27m, 105.84m, 104.33m, 105.57m, 118882L),
            (105.24m, 107.03m, 105.08m, 106.30m, 102767L),
            (106.44m, 108.73m, 105.76m, 108.17m, 130571L),
            (107.90m, 109.43m, 107.58m, 109.40m, 96065L),
            (109.84m, 110.72m, 108.62m, 108.93m, 119326L),
            (109.34m, 109.80m, 108.76m, 109.02m, 94798L),
            (108.78m, 110.18m, 107.88m, 109.60m, 103964L),
            (110.10m, 110.61m, 109.07m, 109.16m, 82827L),
            (109.29m, 110.08m, 107.97m, 108.39m, 83812L),
            (108.89m, 109.42m, 107.46m, 108.43m, 131646L),
            (108.65m, 109.33m, 106.82m, 107.36m, 96010L),
            (106.97m, 108.61m, 106.52m, 108.18m, 137228L),
            (107.94m, 110.21m, 107.76m, 109.71m, 134757L),
            (109.51m, 111.86m, 108.90m, 111.22m, 89171L),
            (111.26m, 113.19m, 110.73m, 112.41m, 80035L),
            (111.93m, 112.86m, 110.80m, 111.68m, 129899L),
            (111.24m, 112.12m, 109.95m, 110.90m, 85140L),
            (110.47m, 111.42m, 109.70m, 110.66m, 87704L),
            (110.71m, 110.98m, 109.52m, 110.39m, 105388L),
            (110.43m, 111.16m, 109.13m, 109.33m, 98703L),
            (109.48m, 111.06m, 108.96m, 110.62m, 87261L),
            (110.46m, 111.05m, 109.36m, 109.59m, 93214L),
            (109.72m, 109.95m, 107.19m, 108.10m, 131578L),
            (107.84m, 108.51m, 106.40m, 106.61m, 87939L),
            (106.68m, 108.19m, 105.90m, 107.72m, 128449L),
            (107.32m, 107.75m, 106.17m, 106.59m, 108021L),
            (106.76m, 108.06m, 106.66m, 107.08m, 104157L),
            (107.44m, 107.69m, 106.21m, 106.40m, 106916L),
            (106.18m, 106.43m, 105.05m, 105.97m, 106587L),
            (106.02m, 106.90m, 105.02m, 106.85m, 130161L),
        };

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        var bars = new List<HistoricalBar>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            bars.Add(new HistoricalBar
            {
                Symbol = Symbol,
                Timestamp = start.AddDays(i),
                Open = raw[i].Open,
                High = raw[i].High,
                Low = raw[i].Low,
                Close = raw[i].Close,
                AdjustedClose = raw[i].Close,
                Volume = raw[i].Volume,
                Source = "Fixture",
                Timeframe = Timeframe.Daily
            });
        }
        return bars;
    }

    public static IReadOnlyList<decimal> Closes { get; } = Bars.Select(b => b.Close).ToList();
    public static IReadOnlyList<long> Volumes { get; } = Bars.Select(b => b.Volume).ToList();

    public static void AssertNear(decimal? expected, decimal? actual, decimal tolerance, string label)
    {
        if (expected is null || actual is null)
        {
            Framework.Assert.Equal(expected is null, actual is null, $"{label}: null-ness mismatch (expected={expected}, actual={actual})");
            return;
        }

        var diff = Math.Abs(expected.Value - actual.Value);
        Framework.Assert.True(diff <= tolerance, $"{label}: expected {expected}, got {actual} (diff {diff} > tolerance {tolerance})");
    }
}
