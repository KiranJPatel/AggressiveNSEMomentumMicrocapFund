using TradingPlatform.Core.Configuration;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class StagedEntryCalculatorTests
{
    private static readonly StrategyOptions Options = new(); // 40/30/30

    [Fact]
    public void Split_EvenlyDivisibleTotal_SplitsExactly()
    {
        var (s1, s2, s3) = StagedEntryCalculator.Split(1000m, Options);
        Assert.Equal(400m, s1);
        Assert.Equal(300m, s2);
        Assert.Equal(300m, s3);
    }

    [Fact]
    public void Split_AlwaysSumsExactlyToTotal_EvenWithRoundingRemainder()
    {
        // 137 is not evenly divisible by 40/30/30 percentages — this is exactly the case that would silently under-allocate without the "Stage3 absorbs the remainder" design.
        var (s1, s2, s3) = StagedEntryCalculator.Split(137m, Options);
        Assert.Equal(137m, s1 + s2 + s3);
    }

    [Fact]
    public void Split_SmallTotalPronetoRoundingLoss_StillSumsExactly()
    {
        for (var total = 1m; total <= 50m; total++)
        {
            var (s1, s2, s3) = StagedEntryCalculator.Split(total, Options);
            Assert.Equal(total, s1 + s2 + s3);
        }
    }

    [Fact]
    public void Split_ZeroTotal_ReturnsAllZero()
    {
        var (s1, s2, s3) = StagedEntryCalculator.Split(0m, Options);
        Assert.Equal(0m, s1);
        Assert.Equal(0m, s2);
        Assert.Equal(0m, s3);
    }

    [Fact]
    public void Split_NegativeTotal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StagedEntryCalculator.Split(-10m, Options));
    }

    [Fact]
    public void Split_Stage1AndStage2_AreFlooredNotRounded()
    {
        // 133 * 40% = 53.2 -> should floor to 53, not round to 53.
        var (s1, _, _) = StagedEntryCalculator.Split(133m, Options);
        Assert.Equal(53m, s1);
    }
}
