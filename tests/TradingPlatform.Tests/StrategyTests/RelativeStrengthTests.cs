using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.RelativeStrength;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class RelativeStrengthTests
{
    private static HistoricalBar Bar(string symbol, int dayOffset, decimal close) => new()
    {
        Symbol = symbol,
        Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = close,
        Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static List<decimal> GeometricSeries(decimal start, decimal totalReturn, int bars)
    {
        var end = start * (1 + totalReturn);
        var result = new List<decimal>();
        for (var i = 0; i <= bars; i++)
        {
            var t = (decimal)i / bars;
            result.Add(start + (end - start) * t);
        }
        return result;
    }

    // --- PriceSeriesAligner ---

    [Fact]
    public void Aligner_DropsDatesNotPresentInBothSeries()
    {
        var stock = new List<HistoricalBar> { Bar("A", 0, 100), Bar("A", 1, 101), Bar("A", 2, 102) };
        var benchmark = new List<HistoricalBar> { Bar("IDX", 0, 1000), Bar("IDX", 2, 1020) }; // day 1 missing

        var (stockCloses, benchCloses) = PriceSeriesAligner.AlignByDate(stock, benchmark);

        Assert.Equal(2, stockCloses.Count);
        Assert.Equal(100m, stockCloses[0]);
        Assert.Equal(102m, stockCloses[1]);
        Assert.Equal(1000m, benchCloses[0]);
        Assert.Equal(1020m, benchCloses[1]);
    }

    [Fact]
    public void Aligner_EmptyOverlap_ReturnsEmpty()
    {
        var stock = new List<HistoricalBar> { Bar("A", 0, 100) };
        var benchmark = new List<HistoricalBar> { Bar("IDX", 5, 1000) };

        var (stockCloses, benchCloses) = PriceSeriesAligner.AlignByDate(stock, benchmark);
        Assert.Equal(0, stockCloses.Count);
        Assert.Equal(0, benchCloses.Count);
    }

    // --- RelativeStrengthCalculator ---

    [Fact]
    public void Calculate_StockOutperforms_ScoreAboveFifty_MatchesReferenceValue()
    {
        var options = new RsOptions { Periods = new[] { 90 }, PeriodWeights = new[] { 100m }, NormalizationScalePercent = 20m };
        var stockCloses = GeometricSeries(100m, 0.30m, 90);   // +30%
        var benchCloses = GeometricSeries(1000m, 0.10m, 90);  // +10%

        var result = RelativeStrengthCalculator.Calculate(stockCloses, benchCloses, options);

        AssertNear(18.181818m, result.Periods[0].RawOutperformancePercent, 0.001m, "raw outperformance");
        AssertNear(86.034782m, result.Periods[0].NormalizedScore, 0.001m, "normalized score");
        AssertNear(86.034782m, result.CompositeScore, 0.001m, "composite score");
        Assert.True(result.CompositeScore > 50m);
    }

    [Fact]
    public void Calculate_StockUnderperforms_ScoreBelowFifty()
    {
        var options = new RsOptions { Periods = new[] { 90 }, PeriodWeights = new[] { 100m }, NormalizationScalePercent = 20m };
        var stockCloses = GeometricSeries(100m, -0.10m, 90);
        var benchCloses = GeometricSeries(1000m, 0.10m, 90);

        var result = RelativeStrengthCalculator.Calculate(stockCloses, benchCloses, options);

        AssertNear(13.965218m, result.Periods[0].NormalizedScore, 0.001m, "normalized score");
        Assert.True(result.CompositeScore < 50m);
    }

    [Fact]
    public void Calculate_StockMatchesBenchmarkExactly_ScoreIsFifty()
    {
        var options = new RsOptions { Periods = new[] { 50 }, PeriodWeights = new[] { 100m } };
        var stockCloses = GeometricSeries(100m, 0.15m, 50);
        var benchCloses = GeometricSeries(1000m, 0.15m, 50); // identical % return

        var result = RelativeStrengthCalculator.Calculate(stockCloses, benchCloses, options);

        AssertNear(0m, result.Periods[0].RawOutperformancePercent, 0.001m, "raw outperformance (should be ~0)");
        AssertNear(50m, result.Periods[0].NormalizedScore, 0.001m, "normalized score (should be exactly 50)");
    }

    [Fact]
    public void Calculate_InsufficientHistoryForPeriod_ReturnsNeutralNotFabricatedPositive()
    {
        var options = new RsOptions { Periods = new[] { 200 }, PeriodWeights = new[] { 100m } };
        var stockCloses = GeometricSeries(100m, 0.50m, 30); // only 31 bars, period needs 201
        var benchCloses = GeometricSeries(1000m, 0.10m, 30);

        var result = RelativeStrengthCalculator.Calculate(stockCloses, benchCloses, options);

        Assert.Equal(0m, result.Periods[0].RawOutperformancePercent);
        Assert.Equal(50m, result.Periods[0].NormalizedScore);
    }

    [Fact]
    public void Calculate_MultiPeriodComposite_MatchesWeightedAverage()
    {
        var options = new RsOptions { Periods = new[] { 20, 40 }, PeriodWeights = new[] { 70m, 30m }, NormalizationScalePercent = 20m };
        // Build a 41-bar series where the 20-bar window and 40-bar window give different returns.
        var stockCloses = new List<decimal>();
        var benchCloses = new List<decimal>();
        for (var i = 0; i <= 40; i++)
        {
            // Two-phase growth: flat for first 20 bars, then stock rallies hard in the second half.
            var stockPrice = i <= 20 ? 100m : 100m + (i - 20) * 3m;
            var benchPrice = 1000m + i * 2m; // steady benchmark growth throughout
            stockCloses.Add(stockPrice);
            benchCloses.Add(benchPrice);
        }

        var result = RelativeStrengthCalculator.Calculate(stockCloses, benchCloses, options);
        Assert.Equal(2, result.Periods.Count);

        var expectedComposite = (result.Periods[0].NormalizedScore * 70m + result.Periods[1].NormalizedScore * 30m) / 100m;
        AssertNear(expectedComposite, result.CompositeScore, 0.001m, "composite matches manual weighted average");
    }

    // --- RelativeStrengthEngine (multi-benchmark) ---

    [Fact]
    public void Engine_SingleBenchmark_BlendedScoreEqualsThatBenchmarksComposite()
    {
        var stockBars = Enumerable.Range(0, 51).Select(i => Bar("STK", i, 100m + i)).ToList();
        var broadBars = Enumerable.Range(0, 51).Select(i => Bar("NIFTY500", i, 1000m + i)).ToList();
        var options = new RsOptions { Periods = new[] { 50 }, PeriodWeights = new[] { 100m } };

        var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>> { [BenchmarkNames.BroadIndex] = broadBars };
        var result = RelativeStrengthEngine.Calculate(stockBars, benchmarks, options);

        Assert.Equal(1, result.ByBenchmark.Count);
        AssertNear(result.ByBenchmark[0].Result.CompositeScore, result.BlendedScore, 0.0001m, "blended == single benchmark composite");
    }

    [Fact]
    public void Engine_TwoBenchmarks_RenormalizesWeights_MidcapAbsent()
    {
        var stockBars = Enumerable.Range(0, 51).Select(i => Bar("STK", i, 100m + i * 2m)).ToList();
        var broadBars = Enumerable.Range(0, 51).Select(i => Bar("NIFTY500", i, 1000m + i)).ToList();
        var sectorBars = Enumerable.Range(0, 51).Select(i => Bar("SECTOR", i, 500m + i * 1.5m)).ToList();
        var options = new RsOptions { Periods = new[] { 50 }, PeriodWeights = new[] { 100m }, BroadIndexWeight = 50m, SectorBenchmarkWeight = 25m, MidcapBenchmarkWeight = 25m };

        var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>>
        {
            [BenchmarkNames.BroadIndex] = broadBars,
            [BenchmarkNames.Sector] = sectorBars,
        };
        var result = RelativeStrengthEngine.Calculate(stockBars, benchmarks, options);

        // No midcap supplied -> blend should be (broad*50 + sector*25) / 75, not divided by all 100.
        var broadScore = result.ByBenchmark.Single(b => b.BenchmarkName == BenchmarkNames.BroadIndex).Result.CompositeScore;
        var sectorScore = result.ByBenchmark.Single(b => b.BenchmarkName == BenchmarkNames.Sector).Result.CompositeScore;
        var expected = (broadScore * 50m + sectorScore * 25m) / 75m;

        AssertNear(expected, result.BlendedScore, 0.0001m, "renormalized two-benchmark blend");
    }

    [Fact]
    public void Engine_NoBenchmarks_ReturnsNeutralFifty()
    {
        var stockBars = new List<HistoricalBar> { Bar("STK", 0, 100) };
        var result = RelativeStrengthEngine.Calculate(stockBars, new Dictionary<string, IReadOnlyList<HistoricalBar>>(), new RsOptions());
        Assert.Equal(0, result.ByBenchmark.Count);
        Assert.Equal(50m, result.BlendedScore);
    }

    private static void AssertNear(decimal expected, decimal actual, decimal tolerance, string label)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance, $"{label}: expected {expected}, got {actual} (diff {diff} > tolerance {tolerance})");
    }
}
