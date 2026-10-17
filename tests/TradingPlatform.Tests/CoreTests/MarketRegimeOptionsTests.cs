using TradingPlatform.Core.Configuration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CoreTests;

public class MarketRegimeOptionsTests
{
    [Fact]
    public void RegimeThresholds_AreStrictlyDescending()
    {
        var o = new MarketRegimeOptions();
        Assert.True(o.AggressiveThreshold > o.NormalThreshold);
        Assert.True(o.NormalThreshold > o.CautiousThreshold);
        Assert.True(o.CautiousThreshold > o.DefensiveThreshold);
    }

    [Fact]
    public void ExposurePercentages_AreStrictlyDescendingWithRegimeQuality()
    {
        var o = new MarketRegimeOptions();
        Assert.True(o.AggressiveExposurePercent > o.NormalExposurePercent);
        Assert.True(o.NormalExposurePercent > o.CautiousExposurePercent);
        Assert.True(o.CautiousExposurePercent > o.DefensiveExposurePercent);
        Assert.True(o.DefensiveExposurePercent > o.EmergencyExposurePercent);
    }
}
