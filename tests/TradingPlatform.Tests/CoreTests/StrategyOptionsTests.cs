using TradingPlatform.Core.Configuration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CoreTests;

public class StrategyOptionsTests
{
    [Fact]
    public void ScoringWeights_SumToOneHundred()
    {
        var w = new ScoringWeights();
        var total = w.RelativeStrength + w.VolumeAccumulation + w.PriceMomentum
            + w.EarningsAcceleration + w.VcpQuality + w.RevenueAcceleration
            + w.TrendStructure + w.InstitutionalActivity + w.SectorStrength;

        Assert.Equal(100m, total, $"Scoring weights must sum to 100 per spec §3.2, got {total}");
    }

    [Fact]
    public void StagedEntryPercentages_SumToOneHundred()
    {
        var o = new StrategyOptions();
        var total = o.Stage1PercentOfPosition + o.Stage2PercentOfPosition + o.Stage3PercentOfPosition;
        Assert.Equal(100m, total, $"Staged entry percentages must sum to 100 per spec §3.7, got {total}");
    }

    [Fact]
    public void Stage2TriggerRange_MinNotGreaterThanMax()
    {
        var o = new StrategyOptions();
        Assert.True(o.Stage2TriggerFavorableMoveMinPercent <= o.Stage2TriggerFavorableMoveMaxPercent);
    }

    [Fact]
    public void TrailingLadder_TriggersAreStrictlyIncreasing()
    {
        var o = new StrategyOptions();
        Assert.True(o.TrailingBreakevenTriggerR < o.TrailingActiveTriggerR);
        Assert.True(o.TrailingActiveTriggerR < o.TrailingAggressiveTriggerR);
    }
}
