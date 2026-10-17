using TradingPlatform.Core.Enums;
using TradingPlatform.Reporting;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

public class CapitalGrowthSimulatorTests
{
    [Fact]
    public void Simulate_DefaultScenarios_Produces21Combinations()
    {
        var scenarios = CapitalGrowthSimulator.Simulate(1_000_000m);
        Assert.Equal(7 * 3, scenarios.Count); // 7 return rates x 3 horizons
    }

    [Fact]
    public void Simulate_20PercentFor3Years_CompoundsCorrectly()
    {
        var scenarios = CapitalGrowthSimulator.Simulate(1_000_000m, new decimal[] { 20m }, new[] { 3 });
        var expected = 1_000_000m * (decimal)Math.Pow(1.2, 3);
        Assert.Equal(Math.Round(expected, 6), Math.Round(scenarios[0].ProjectedCapital, 6));
    }

    [Fact]
    public void Simulate_ZeroYears_ReturnsInitialCapitalUnchanged()
    {
        var scenarios = CapitalGrowthSimulator.Simulate(1_000_000m, new decimal[] { 50m }, new[] { 0 });
        Assert.Equal(1_000_000m, scenarios[0].ProjectedCapital);
    }

    [Fact]
    public void Simulate_HigherReturnRate_ProducesHigherProjection_AllElseEqual()
    {
        var scenarios = CapitalGrowthSimulator.Simulate(1_000_000m, new decimal[] { 20m, 50m }, new[] { 5 });
        Assert.True(scenarios[1].ProjectedCapital > scenarios[0].ProjectedCapital);
    }
}

public class RegimeBucketMapperTests
{
    [Fact]
    public void Aggressive_MapsToBull()
    {
        Assert.Equal("Bull", RegimeBucketMapper.ToReportBucket(RegimeClassification.Aggressive));
    }

    [Fact]
    public void Normal_MapsToBull()
    {
        Assert.Equal("Bull", RegimeBucketMapper.ToReportBucket(RegimeClassification.Normal));
    }

    [Fact]
    public void Cautious_MapsToNormal()
    {
        Assert.Equal("Normal", RegimeBucketMapper.ToReportBucket(RegimeClassification.Cautious));
    }

    [Fact]
    public void Defensive_MapsToBear()
    {
        Assert.Equal("Bear", RegimeBucketMapper.ToReportBucket(RegimeClassification.Defensive));
    }

    [Fact]
    public void Emergency_MapsToBear()
    {
        Assert.Equal("Bear", RegimeBucketMapper.ToReportBucket(RegimeClassification.Emergency));
    }
}
