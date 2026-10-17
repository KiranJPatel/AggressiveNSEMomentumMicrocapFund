using TradingPlatform.Core.Configuration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CoreTests;

public class RiskOptionsTests
{
    [Fact]
    public void RiskPercentPerTrade_DoesNotExceedConfiguredMax()
    {
        var o = new RiskOptions();
        Assert.True(o.RiskPercentPerTrade <= o.MaxRiskPercentPerTrade,
            $"Default RiskPercentPerTrade ({o.RiskPercentPerTrade}) must not exceed MaxRiskPercentPerTrade ({o.MaxRiskPercentPerTrade}) per spec §4.1.");
    }

    [Fact]
    public void StopAtrMultiplierRange_MinNotGreaterThanMax()
    {
        var o = new RiskOptions();
        Assert.True(o.StopAtrMultiplierMin <= o.StopAtrMultiplierMax);
    }

    [Fact]
    public void DrawdownTiers_PropertyDefaultsEmpty_ToAvoidConfigBinderDuplication()
    {
        // See RiskOptions.DrawdownTiers XML doc: ConfigurationBinder appends
        // to a pre-populated List<T> instead of replacing it, so the bindable
        // property must start empty. Regression guard for that fix.
        var o = new RiskOptions();
        Assert.Equal(0, o.DrawdownTiers.Count);
    }

    [Fact]
    public void DefaultDrawdownTiers_AreOrderedAscendingByThreshold()
    {
        var tiers = RiskOptions.DefaultDrawdownTiers;
        for (var i = 1; i < tiers.Count; i++)
        {
            Assert.True(
                tiers[i].ThresholdPercent > tiers[i - 1].ThresholdPercent,
                $"DrawdownTiers must be strictly ascending by ThresholdPercent (spec §4.5); " +
                $"tier {i} ({tiers[i].Label}={tiers[i].ThresholdPercent}) is not > " +
                $"tier {i - 1} ({tiers[i - 1].Label}={tiers[i - 1].ThresholdPercent}).");
        }
    }

    [Fact]
    public void DefaultDrawdownTiers_FortyPercentTierIsShutdown()
    {
        var shutdownTier = RiskOptions.DefaultDrawdownTiers.Single(t => t.ThresholdPercent == 40);
        Assert.True(shutdownTier.IsShutdown, "40% drawdown must be flagged as a trading shutdown per spec §4.5.");
    }

    [Fact]
    public void DefaultDrawdownTiers_OnlyTheFortyPercentTierIsShutdown()
    {
        var shutdownTiers = RiskOptions.DefaultDrawdownTiers.Where(t => t.IsShutdown).ToList();
        Assert.Equal(1, shutdownTiers.Count);
        Assert.Equal(40m, shutdownTiers[0].ThresholdPercent);
    }

    [Fact]
    public void MonthlyLossLimitReduction_IsDeeperThanWeekly()
    {
        // "Defensive mode" (monthly) must be at least as severe a cut as
        // "reduce risk" (weekly) — spec §4.5's own escalating language
        // (daily stop < weekly reduce < monthly defensive) requires this
        // ordering even though neither magnitude is spec-numeric.
        var o = new RiskOptions();
        Assert.True(o.MonthlyLossLimitRiskReductionPercent >= o.WeeklyLossLimitRiskReductionPercent);
    }
}
