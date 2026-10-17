using TradingPlatform.Indicators.Trend;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class TrendTests
{
    [Fact]
    public void Adx14_PlusMinusDi_MatchReferenceValues()
    {
        var result = AverageDirectionalIndex.Calculate(Bars, 14);

        (int Index, decimal? Expected)[] plusDiCases =
        {
            (14, 36.9720m), (19, 26.7752m), (20, 24.1504m), (25, 29.2210m),
            (30, 20.5109m), (35, 13.9388m), (39, 13.6956m),
        };
        foreach (var (i, expected) in plusDiCases)
            AssertNear(expected, result.PlusDi[i], LooseTolerance, $"+DI14[{i}]");

        (int Index, decimal? Expected)[] minusDiCases =
        {
            (14, 5.5783m), (19, 12.8057m), (20, 14.0500m), (25, 9.6358m),
            (30, 12.2639m), (35, 18.6071m), (39, 20.1950m),
        };
        foreach (var (i, expected) in minusDiCases)
            AssertNear(expected, result.MinusDi[i], LooseTolerance, $"-DI14[{i}]");
    }

    [Fact]
    public void Adx14_MatchesReferenceValues()
    {
        var result = AverageDirectionalIndex.Calculate(Bars, 14);
        (int Index, decimal? Expected)[] cases =
        {
            (14, null), (19, null), (20, null), (25, null),
            (30, 43.8555m), (35, 34.3071m), (39, 30.0982m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result.Adx[i], LooseTolerance, $"ADX14[{i}]");
    }

    [Fact]
    public void Adx_IsNeverNegativeOrAbove100()
    {
        var result = AverageDirectionalIndex.Calculate(Bars, 14);
        foreach (var v in result.Adx)
        {
            if (v is null) continue;
            Assert.True(v.Value is >= 0m and <= 100m, $"ADX value {v} out of [0,100] bounds");
        }
    }

    [Fact]
    public void Supertrend_ValueAndTrend_MatchReferenceValues()
    {
        var result = SupertrendIndicator.Calculate(Bars, 10, 3.0m);

        (int Index, decimal? Expected)[] valueCases =
        {
            (14, 104.262531m), (19, 104.562235m), (20, 104.562235m), (25, 105.668375m),
            (30, 105.668375m), (35, 105.668375m), (39, 105.668375m),
        };
        foreach (var (i, expected) in valueCases)
            AssertNear(expected, result[i].Value, LooseTolerance, $"Supertrend value[{i}]");

        (int Index, bool? Expected)[] trendCases =
        {
            (14, true), (19, true), (20, true), (25, true), (30, true), (35, true), (39, true),
        };
        foreach (var (i, expected) in trendCases)
            Assert.Equal(expected, result[i].IsUpTrend);
    }

    [Fact]
    public void Supertrend_LowerBand_NeverDecreasesWhileInUptrend()
    {
        // The ratchet property: in an uptrend, the active (lower) band can rise or hold, never fall.
        var result = SupertrendIndicator.Calculate(Bars, 10, 3.0m);
        decimal? lastUptrendValue = null;

        foreach (var point in result)
        {
            if (point.IsUpTrend != true) { lastUptrendValue = null; continue; }
            if (lastUptrendValue is not null)
                Assert.True(point.Value!.Value >= lastUptrendValue.Value - Tolerance, "Supertrend lower band decreased during an uptrend");
            lastUptrendValue = point.Value;
        }
    }
}
