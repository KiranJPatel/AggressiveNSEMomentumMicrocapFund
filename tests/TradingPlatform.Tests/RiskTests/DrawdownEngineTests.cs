using TradingPlatform.Core.Configuration;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class DrawdownEngineTests
{
    private static readonly RiskOptions Options = new() { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() };
    private static DateOnly D(int day) => new DateOnly(2026, 1, 1).AddDays(day);

    [Fact]
    public void FirstObservation_SetsNavAsPeak_ZeroDrawdown_FullAggressionTier()
    {
        var (state, evt) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);

        Assert.Equal(1_000_000m, state.PeakNav);
        Assert.Equal(0m, state.DrawdownPercent);
        Assert.Equal("FullAggression", state.CurrentTier.Label);
        Assert.False(state.IsTradingShutdown);
        Assert.True(evt is null, "no tier transition to log on the very first observation");
    }

    [Fact]
    public void NavDrop_ComputesDrawdownPercent_Correctly()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 880_000m, Options); // 12% down

        Assert.Equal(12m, s2.DrawdownPercent);
        Assert.Equal(1_000_000m, s2.PeakNav); // peak unchanged
    }

    [Fact]
    public void TierBoundary_ExactlyAtThreshold_SelectsThatTier_NotThePriorOne()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 900_000m, Options); // exactly 10.0% down

        Assert.Equal("NormalReduced", s2.CurrentTier.Label); // the 10% tier, not FullAggression
    }

    [Fact]
    public void DeepDrawdown_SelectsCorrectDeeperTiers()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);

        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 780_000m, Options); // 22% -> ReduceRisk40
        Assert.Equal("ReduceRisk40", s2.CurrentTier.Label);
        Assert.Equal(40m, s2.CurrentTier.RiskReductionPercent);

        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 580_000m, Options); // 42% -> TradingShutdown
        Assert.Equal("TradingShutdown", s3.CurrentTier.Label);
        Assert.True(s3.CurrentTier.IsShutdown);
        Assert.True(s3.IsTradingShutdown);
    }

    [Fact]
    public void NewPeak_ResetsDrawdownToZero_AndClearsEpisode()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 900_000m, Options); // enter drawdown
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 1_050_000m, Options); // new all-time high

        Assert.Equal(0m, s3.DrawdownPercent);
        Assert.Equal(1_050_000m, s3.PeakNav);
        Assert.True(s3.DrawdownStartDate is null);
        Assert.True(s3.TroughDate is null);
    }

    [Fact]
    public void MaxDrawdownPercent_NeverDecreases_EvenAfterRecovery()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 700_000m, Options); // 30% down, new max
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 1_100_000m, Options); // full recovery + new peak
        var (s4, _) = DrawdownEngine.Evaluate(s3, D(3), 1_078_000m, Options); // small 2% dip, well short of the prior max

        Assert.Equal(30m, s2.MaxDrawdownPercent);
        Assert.Equal(30m, s3.MaxDrawdownPercent); // still remembered after recovery
        Assert.Equal(30m, s4.MaxDrawdownPercent); // the new, shallower dip doesn't erase it
    }

    [Fact]
    public void DrawdownDurationDays_MeasuredFromFirstUnderwaterDay()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 950_000m, Options); // day 1 underwater
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(4), 930_000m, Options); // still underwater, 3 days later

        Assert.Equal(0, s2.DrawdownDurationDays);
        Assert.Equal(3, s3.DrawdownDurationDays);
    }

    [Fact]
    public void RecoveryDurationDays_MeasuredFromEpisodeTrough_ToNewPeak()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 900_000m, Options);  // -10%, first trough guess
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 850_000m, Options);  // -15%, deeper trough (day 2)
        var (s4, _) = DrawdownEngine.Evaluate(s3, D(3), 900_000m, Options);  // partial bounce, not yet a new peak
        var (s5, _) = DrawdownEngine.Evaluate(s4, D(9), 1_010_000m, Options); // new peak on day 9

        Assert.Equal(D(2), s3.TroughDate);
        Assert.Equal(7, s5.LastRecoveryDurationDays); // day 9 - day 2
    }

    [Fact]
    public void EpisodeTrough_OnlyDeepensWithinEpisode_NotOnAPartialBounce()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 850_000m, Options);  // -15%, trough so far
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 900_000m, Options);  // bounce to -10%, shallower

        Assert.Equal(D(1), s3.TroughDate); // trough date unchanged by the shallower bounce
        Assert.Equal(15m, s3.EpisodeTroughDrawdownPercent);
    }

    [Fact]
    public void EmitsDrawdownEvent_OnlyOnTierTransition_NotOnRepeatedSameTierCalls()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, evt2) = DrawdownEngine.Evaluate(s1, D(1), 900_000m, Options); // 10% -> NormalReduced, a transition
        var (_, evt3) = DrawdownEngine.Evaluate(s2, D(2), 899_000m, Options); // still ~10.1%, same tier

        Assert.NotNull(evt2);
        Assert.Equal("NormalReduced", evt2!.TierName);
        Assert.True(evt3 is null);
    }

    [Fact]
    public void TradingShutdown_Latches_EvenAfterNavFullyRecovers_WithoutManualReset()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 580_000m, Options); // 42% -> shutdown
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 1_200_000m, Options); // full recovery, new peak

        Assert.True(s2.RequiresManualReset);
        Assert.Equal(0m, s3.DrawdownPercent); // genuinely recovered, on the drawdown axis alone...
        Assert.True(s3.RequiresManualReset);  // ...but the latch is still engaged
        Assert.True(s3.IsTradingShutdown);    // ...so trading is still shut down
    }

    [Fact]
    public void ManualReset_Refused_WhileStillAtShutdownLevelDrawdown()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 580_000m, Options); // 42% -> shutdown
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 590_000m, Options, manualResetAcknowledged: true); // still 41%, still shutdown-tier

        Assert.True(s3.RequiresManualReset, "Reset must be refused while the drawdown is still genuinely at/above the shutdown threshold.");
    }

    [Fact]
    public void ManualReset_Succeeds_OnceBelowShutdownThreshold_AndAcknowledged()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 580_000m, Options); // 42% -> shutdown
        var (s3, _) = DrawdownEngine.Evaluate(s2, D(2), 1_200_000m, Options); // full recovery (still latched)
        var (s4, _) = DrawdownEngine.Evaluate(s3, D(3), 1_200_000m, Options, manualResetAcknowledged: true); // now acknowledge

        Assert.False(s4.RequiresManualReset);
        Assert.False(s4.IsTradingShutdown);
    }

    [Fact]
    public void ManualResetAcknowledged_WithNoActiveLatch_IsHarmless()
    {
        var (s1, _) = DrawdownEngine.Evaluate(null, D(0), 1_000_000m, Options);
        var (s2, _) = DrawdownEngine.Evaluate(s1, D(1), 950_000m, Options, manualResetAcknowledged: true); // never shut down

        Assert.False(s2.RequiresManualReset);
    }
}
