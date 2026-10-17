using TradingPlatform.Indicators.Statistics;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class PearsonCorrelationTests
{
    [Fact]
    public void PerfectPositiveCorrelation_ReturnsOne()
    {
        var x = new List<decimal> { 1, 2, 3, 4, 5 };
        var y = new List<decimal> { 10, 20, 30, 40, 50 };
        var r = PearsonCorrelation.Calculate(x, y);
        AssertNear(1.0m, r!.Value, 0.0001m);
    }

    [Fact]
    public void PerfectNegativeCorrelation_ReturnsMinusOne()
    {
        var x = new List<decimal> { 1, 2, 3, 4, 5 };
        var y = new List<decimal> { 50, 40, 30, 20, 10 };
        var r = PearsonCorrelation.Calculate(x, y);
        AssertNear(-1.0m, r!.Value, 0.0001m);
    }

    [Fact]
    public void MixedSeries_MatchesIndependentlyComputedValue()
    {
        var x = new List<decimal> { 1, 2, 3, 4, 5, 6, 7, 8 };
        var y = new List<decimal> { 3, 1, 4, 1, 5, 9, 2, 6 };
        var r = PearsonCorrelation.Calculate(x, y);
        AssertNear(0.4774552605594227m, r!.Value, 0.0001m);
    }

    [Fact]
    public void ConstantSeries_ReturnsNull_UndefinedNotZero()
    {
        var x = new List<decimal> { 5, 5, 5, 5 };
        var y = new List<decimal> { 1, 2, 3, 4 };
        var r = PearsonCorrelation.Calculate(x, y);
        Assert.True(r is null, "Correlation against a zero-variance series is undefined, not 0.");
    }

    [Fact]
    public void FewerThanTwoPoints_ReturnsNull()
    {
        var r = PearsonCorrelation.Calculate(new List<decimal> { 1 }, new List<decimal> { 2 });
        Assert.True(r is null);
    }

    [Fact]
    public void MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PearsonCorrelation.Calculate(new List<decimal> { 1, 2, 3 }, new List<decimal> { 1, 2 }));
    }

    [Fact]
    public void ToReturns_ComputesSimplePeriodReturns()
    {
        var closes = new List<decimal> { 100m, 110m, 99m };
        var returns = PearsonCorrelation.ToReturns(closes);

        Assert.Equal(2, returns.Count);
        AssertNear(0.10m, returns[0], 0.0001m);   // 110/100 - 1
        AssertNear(-0.10m, returns[1], 0.0001m);  // 99/110 - 1
    }

    private static void AssertNear(decimal expected, decimal actual, decimal tolerance)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance, $"expected {expected}, got {actual} (diff {diff})");
    }
}
