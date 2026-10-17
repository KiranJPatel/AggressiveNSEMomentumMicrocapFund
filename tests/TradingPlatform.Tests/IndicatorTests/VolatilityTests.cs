using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Volatility;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class VolatilityTests
{
    [Fact]
    public void TrueRange_FirstBar_IsHighMinusLow()
    {
        var tr = TrueRange.Calculate(Bars);
        AssertNear(Bars[0].High - Bars[0].Low, tr[0], Tolerance, "TR[0]");
    }

    [Fact]
    public void TrueRange_CapturesGapBeyondPriorClose()
    {
        // A bar that gaps up hard: prior close 100, today's low 105, high 110.
        // TR must be High-PriorClose (10), not just High-Low (5).
        var bars = new List<HistoricalBar>
        {
            MakeBar(0, 100, 101, 99, 100),
            MakeBar(1, 110, 110, 105, 108),
        };
        var tr = TrueRange.Calculate(bars);
        AssertNear(10m, tr[1], Tolerance, "TR[1] (gap case)");
    }

    [Fact]
    public void Atr14_MatchesReferenceValues()
    {
        var result = AverageTrueRange.Calculate(Bars, 14);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 1.759745m), (19, 1.776421m), (20, 1.828820m), (25, 2.006260m),
            (30, 1.973211m), (35, 2.004517m), (39, 1.891328m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], LooseTolerance, $"ATR14[{i}]");
    }

    [Fact]
    public void AtrPercent14_MatchesReferenceValues()
    {
        var result = AverageTrueRange.CalculatePercent(Bars, 14);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 1.6155m), (19, 1.6383m), (20, 1.7034m), (25, 1.7964m),
            (30, 1.7838m), (35, 1.8806m), (39, 1.7701m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], LooseTolerance, $"ATR%14[{i}]");
    }

    [Fact]
    public void Atr_IsNeverNegative()
    {
        var result = AverageTrueRange.Calculate(Bars, 14);
        foreach (var v in result)
        {
            if (v is null) continue;
            Assert.True(v.Value >= 0m, $"ATR value {v} is negative");
        }
    }

    [Fact]
    public void HistoricalVolatility20_MatchesReferenceValues()
    {
        var result = TradingPlatform.Indicators.Volatility.HistoricalVolatility.Calculate(Closes, 20, 252);
        (int Index, decimal? Expected)[] cases =
        {
            (14, null), (19, null), (20, 12.0105m), (25, 12.9482m),
            (30, 13.9660m), (35, 15.1991m), (39, 15.4863m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], LooseTolerance, $"HV20[{i}]");
    }

    [Fact]
    public void HistoricalVolatility_ConstantPrices_IsZero()
    {
        var closes = Enumerable.Repeat(100m, 25).ToList();
        var result = TradingPlatform.Indicators.Volatility.HistoricalVolatility.Calculate(closes, 20, 252);
        Assert.Equal(0m, result[24]!.Value);
    }

    private static HistoricalBar MakeBar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "GAP", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = open, High = high, Low = low, Close = close, AdjustedClose = close,
        Volume = 10000, Source = "Fixture", Timeframe = Timeframe.Daily
    };
}
