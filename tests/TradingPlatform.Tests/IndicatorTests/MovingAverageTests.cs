using TradingPlatform.Indicators.MovingAverages;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class MovingAverageTests
{
    [Fact]
    public void Sma20_MatchesReferenceValues()
    {
        var result = SimpleMovingAverage.Calculate(Closes, 20);
        (int Index, decimal? Expected)[] cases =
        {
            (14, null), (19, 105.789000m), (20, 106.116000m), (25, 108.219500m),
            (30, 109.493000m), (35, 109.332500m), (39, 108.868500m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], Tolerance, $"SMA20[{i}]");
    }

    [Fact]
    public void Wma5_MatchesReferenceValues()
    {
        var result = WeightedMovingAverage.Calculate(Closes, 5);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 108.328667m), (19, 108.760667m), (20, 108.240667m), (25, 111.286667m),
            (30, 110.254000m), (35, 107.296667m), (39, 106.538667m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], Tolerance, $"WMA5[{i}]");
    }

    [Fact]
    public void Ema12_MatchesReferenceValues()
    {
        var result = ExponentialMovingAverage.Calculate(Closes, 12);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 105.753135m), (19, 107.499672m), (20, 107.478184m), (25, 109.442153m),
            (30, 109.941647m), (35, 108.570438m), (39, 107.589247m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], Tolerance, $"EMA12[{i}]");
    }

    [Fact]
    public void Hma9_MatchesReferenceValues()
    {
        var result = HullMovingAverage.Calculate(Closes, 9);
        (int Index, decimal? Expected)[] cases =
        {
            (14, 108.713667m), (19, 109.031074m), (20, 108.387593m), (25, 111.616741m),
            (30, 110.076593m), (35, 106.894407m), (39, 106.196741m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], Tolerance, $"HMA9[{i}]");
    }

    [Fact]
    public void Sma_NullsBeforeWarmup_ValuesAfter()
    {
        var result = SimpleMovingAverage.Calculate(Closes, 20);
        for (var i = 0; i < 19; i++) Assert.True(result[i] is null, $"SMA20[{i}] should be null (warm-up)");
        for (var i = 19; i < result.Count; i++) Assert.True(result[i] is not null, $"SMA20[{i}] should be non-null");
    }

    [Fact]
    public void Sma_HandComputedThreeValueCase()
    {
        // Trivial hand-checkable case, independent of the fixture/Python cross-check.
        var result = SimpleMovingAverage.Calculate(new List<decimal> { 10m, 20m, 30m }, 3);
        Assert.Equal(20m, result[2]!.Value);
    }
}
