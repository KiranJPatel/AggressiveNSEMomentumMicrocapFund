using TradingPlatform.Core.Configuration;
using TradingPlatform.Indicators;
using TradingPlatform.Indicators.MovingAverages;
using TradingPlatform.Indicators.Momentum;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.IndicatorTests.IndicatorFixture;

namespace TradingPlatform.Tests.IndicatorTests;

public class IndicatorEngineTests
{
    [Fact]
    public void ComputeSeries_ProducesOneSnapshotPerBar()
    {
        var snapshots = IndicatorEngine.ComputeSeries(Bars, new IndicatorOptions());
        Assert.Equal(Bars.Count, snapshots.Count);
    }

    [Fact]
    public void ComputeSeries_PropagatesSymbolTimestampTimeframe()
    {
        var snapshots = IndicatorEngine.ComputeSeries(Bars, new IndicatorOptions());
        for (var i = 0; i < Bars.Count; i++)
        {
            Assert.Equal(Bars[i].Symbol, snapshots[i].Symbol);
            Assert.Equal(Bars[i].Timestamp, snapshots[i].Timestamp);
            Assert.Equal(Bars[i].Timeframe, snapshots[i].Timeframe);
        }
    }

    [Fact]
    public void ComputeSeries_Sma20MatchesStandaloneCalculation()
    {
        var options = new IndicatorOptions();
        var snapshots = IndicatorEngine.ComputeSeries(Bars, options);
        var standalone = SimpleMovingAverage.Calculate(Closes, options.Sma20Period);

        for (var i = 0; i < Bars.Count; i++)
            AssertNear(standalone[i], snapshots[i].Sma20, 0.000001m, $"Engine Sma20[{i}] vs standalone");
    }

    [Fact]
    public void ComputeSeries_RsiMatchesStandaloneCalculation()
    {
        var options = new IndicatorOptions();
        var snapshots = IndicatorEngine.ComputeSeries(Bars, options);
        var standalone = RelativeStrengthIndex.Calculate(Closes, options.RsiPeriod);

        for (var i = 0; i < Bars.Count; i++)
            AssertNear(standalone[i], snapshots[i].Rsi, 0.000001m, $"Engine Rsi[{i}] vs standalone");
    }

    [Fact]
    public void ComputeSeries_EmptyInput_ReturnsEmpty()
    {
        var snapshots = IndicatorEngine.ComputeSeries(Array.Empty<TradingPlatform.Core.Models.HistoricalBar>(), new IndicatorOptions());
        Assert.Equal(0, snapshots.Count);
    }

    [Fact]
    public void ComputeSeries_LastSnapshot_HasAllValuesPopulated_GivenEnoughWarmup()
    {
        // With 40 bars and default periods (max is Sma200, unreachable here),
        // shorter-period indicators should all be populated by the last bar.
        var snapshots = IndicatorEngine.ComputeSeries(Bars, new IndicatorOptions());
        var last = snapshots[^1];

        Assert.True(last.HmaFast.HasValue, "HmaFast should be populated");
        Assert.True(last.Rsi.HasValue, "Rsi should be populated");
        Assert.True(last.Atr.HasValue, "Atr should be populated");
        Assert.True(last.Supertrend.HasValue, "Supertrend should be populated");
        Assert.True(last.VolumeSma.HasValue, "VolumeSma should be populated");

        // Sma200 needs 200 bars; this fixture only has 40 — must stay null, not throw or fabricate a value.
        Assert.True(last.Sma200 is null, "Sma200 should remain null with only 40 bars of history");
    }
}
