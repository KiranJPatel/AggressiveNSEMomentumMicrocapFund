using TradingPlatform.Indicators.Momentum;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class MomentumTests
{
    [Fact]
    public void Rsi14_MatchesReferenceValues()
    {
        var result = RelativeStrengthIndex.Calculate(Closes, 14);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 87.1677m), (19, 77.5930m), (20, 68.9541m), (25, 75.9631m),
            (30, 65.3275m), (35, 46.3897m), (39, 48.2525m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], LooseTolerance, $"RSI14[{i}]");
    }

    [Fact]
    public void Rsi_AllGains_Is100_NoDivisionByZero()
    {
        var closes = Enumerable.Range(0, 20).Select(i => 100m + i).ToList(); // strictly rising
        var result = RelativeStrengthIndex.Calculate(closes, 14);
        Assert.Equal(100m, result[14]!.Value);
    }

    [Fact]
    public void Rsi_AllLosses_IsZero()
    {
        var closes = Enumerable.Range(0, 20).Select(i => 100m - i).ToList(); // strictly falling
        var result = RelativeStrengthIndex.Calculate(closes, 14);
        Assert.Equal(0m, result[14]!.Value);
    }

    [Fact]
    public void Rsi_StaysWithinZeroToHundredBounds()
    {
        var result = RelativeStrengthIndex.Calculate(Closes, 14);
        foreach (var v in result)
        {
            if (v is null) continue;
            Assert.True(v.Value is >= 0m and <= 100m, $"RSI value {v} out of [0,100] bounds");
        }
    }

    [Fact]
    public void Macd_MatchesReferenceValues()
    {
        var result = Macd.Calculate(Closes, 12, 26, 9);
        (int Index, decimal? Expected)[] lineCase =
        {
            (14, null), (19, null), (20, null), (25, 2.659846m),
            (30, 2.019003m), (35, 0.742557m), (39, 0.095886m),
        };
        foreach (var (i, expected) in lineCase)
            AssertNear(expected, result.MacdLine[i], Tolerance, $"MACD line[{i}]");

        (int Index, decimal? Expected)[] signalCase =
        {
            (35, 1.644397m), (39, 0.827141m),
        };
        foreach (var (i, expected) in signalCase)
            AssertNear(expected, result.SignalLine[i], Tolerance, $"MACD signal[{i}]");

        (int Index, decimal? Expected)[] histCase =
        {
            (35, -0.901840m), (39, -0.731255m),
        };
        foreach (var (i, expected) in histCase)
            AssertNear(expected, result.Histogram[i], Tolerance, $"MACD histogram[{i}]");
    }

    [Fact]
    public void Macd_HistogramEqualsLineMinusSignal_Always()
    {
        var result = Macd.Calculate(Closes, 12, 26, 9);
        for (var i = 0; i < Closes.Count; i++)
        {
            if (result.MacdLine[i] is null || result.SignalLine[i] is null) continue;
            var expectedHist = result.MacdLine[i]!.Value - result.SignalLine[i]!.Value;
            AssertNear(expectedHist, result.Histogram[i], 0.000001m, $"Histogram consistency[{i}]");
        }
    }
}
