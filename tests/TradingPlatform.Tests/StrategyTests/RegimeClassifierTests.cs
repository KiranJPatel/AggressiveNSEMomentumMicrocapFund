using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class RegimeClassifierTests
{
    private static readonly MarketRegimeOptions Options = new();

    [Fact]
    public void ExactlyAtAggressiveThreshold_IsAggressive()
    {
        var (classification, exposure) = RegimeClassifier.Classify(80, Options);
        Assert.Equal(RegimeClassification.Aggressive, classification);
        Assert.Equal(100m, exposure);
    }

    [Fact]
    public void OneBelowAggressiveThreshold_IsNormal()
    {
        var (classification, _) = RegimeClassifier.Classify(79, Options);
        Assert.Equal(RegimeClassification.Normal, classification);
    }

    [Fact]
    public void ExactlyAtNormalThreshold_IsNormal()
    {
        var (classification, exposure) = RegimeClassifier.Classify(65, Options);
        Assert.Equal(RegimeClassification.Normal, classification);
        Assert.Equal(80m, exposure);
    }

    [Fact]
    public void ExactlyAtCautiousThreshold_IsCautious()
    {
        var (classification, exposure) = RegimeClassifier.Classify(50, Options);
        Assert.Equal(RegimeClassification.Cautious, classification);
        Assert.Equal(50m, exposure);
    }

    [Fact]
    public void ExactlyAtDefensiveThreshold_IsDefensive()
    {
        var (classification, exposure) = RegimeClassifier.Classify(35, Options);
        Assert.Equal(RegimeClassification.Defensive, classification);
        Assert.Equal(25m, exposure);
    }

    [Fact]
    public void OneBelowDefensiveThreshold_IsEmergency()
    {
        var (classification, exposure) = RegimeClassifier.Classify(34, Options);
        Assert.Equal(RegimeClassification.Emergency, classification);
        Assert.Equal(10m, exposure);
    }

    [Fact]
    public void ScoreOfZero_IsEmergency()
    {
        var (classification, _) = RegimeClassifier.Classify(0, Options);
        Assert.Equal(RegimeClassification.Emergency, classification);
    }

    [Fact]
    public void ScoreOfOneHundred_IsAggressive()
    {
        var (classification, _) = RegimeClassifier.Classify(100, Options);
        Assert.Equal(RegimeClassification.Aggressive, classification);
    }
}
