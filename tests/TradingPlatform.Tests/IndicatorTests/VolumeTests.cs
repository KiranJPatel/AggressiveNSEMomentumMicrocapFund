using TradingPlatform.Indicators.Volume;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class VolumeTests
{
    [Fact]
    public void VolumeSma20_MatchesReferenceValues()
    {
        var result = VolumeIndicators.VolumeSma(Volumes, 20);
        (int Index, decimal? Expected)[] cases =
        {
            (14, null), (19, 106385.10m), (20, 104976.20m), (25, 106789.40m),
            (30, 103853.60m), (35, 104137.30m), (39, 106415.90m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], 0.02m, $"VolumeSma20[{i}]");
    }

    [Fact]
    public void RelativeVolume20_MatchesReferenceValues()
    {
        var result = VolumeIndicators.RelativeVolume(Volumes, 20);
        (int Index, decimal? Expected)[] cases =
        {
            (14, null), (19, 1.237448m), (20, 0.914588m), (25, 1.216404m),
            (30, 0.840231m), (35, 1.037294m), (39, 1.223135m),
        };
        foreach (var (i, expected) in cases)
            AssertNear(expected, result[i], Tolerance, $"RelativeVolume20[{i}]");
    }

    [Fact]
    public void RelativeVolume_AboveOne_WhenVolumeExceedsAverage()
    {
        var volumes = new List<long> { 100, 100, 100, 100, 100, 500 }; // spike on the last bar
        var result = VolumeIndicators.RelativeVolume(volumes, 5);
        Assert.True(result[5]!.Value > 1m, "Expected relative volume > 1 on a volume spike");
    }
}
