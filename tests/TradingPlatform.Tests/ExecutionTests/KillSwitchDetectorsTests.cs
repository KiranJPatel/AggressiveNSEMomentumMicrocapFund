using TradingPlatform.Execution.Orchestration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class KillSwitchDetectorsTests
{
    [Fact]
    public void RepeatedRejection_TrailingConsecutiveRejections_MeetsThreshold_Triggers()
    {
        var outcomes = new[] { false, false, true, true, true }; // 3 trailing rejections
        Assert.True(RepeatedRejectionDetector.ShouldTriggerKillSwitch(outcomes, consecutiveThreshold: 3));
    }

    [Fact]
    public void RepeatedRejection_BelowThreshold_DoesNotTrigger()
    {
        var outcomes = new[] { true, true, false, true, true }; // only 2 trailing rejections (the run breaks at index 2)
        Assert.False(RepeatedRejectionDetector.ShouldTriggerKillSwitch(outcomes, consecutiveThreshold: 3));
    }

    [Fact]
    public void RepeatedRejection_OldRejectionsDontCount_OnlyTrailingRun()
    {
        // 4 rejections in a row long ago, but the most RECENT outcome is a success — should not trigger.
        var outcomes = new[] { true, true, true, true, false };
        Assert.False(RepeatedRejectionDetector.ShouldTriggerKillSwitch(outcomes, consecutiveThreshold: 3));
    }

    [Fact]
    public void RepeatedRejection_EmptyHistory_NeverTriggers()
    {
        Assert.False(RepeatedRejectionDetector.ShouldTriggerKillSwitch(Array.Empty<bool>(), consecutiveThreshold: 3));
    }

    [Fact]
    public void AbnormalSlippage_TrailingConsecutiveAbnormalFills_Triggers()
    {
        var slippage = new decimal[] { 10m, 20m, 150m, 200m, 180m }; // 3 trailing fills > 100bps threshold
        Assert.True(AbnormalSlippageDetector.ShouldTriggerKillSwitch(slippage, abnormalThresholdBps: 100m, consecutiveThreshold: 3));
    }

    [Fact]
    public void AbnormalSlippage_BelowThreshold_DoesNotTrigger()
    {
        var slippage = new decimal[] { 10m, 20m, 30m, 40m, 50m };
        Assert.False(AbnormalSlippageDetector.ShouldTriggerKillSwitch(slippage, abnormalThresholdBps: 100m, consecutiveThreshold: 3));
    }

    [Fact]
    public void AbnormalSlippage_OnlyTrailingRun_Counts()
    {
        var slippage = new decimal[] { 200m, 200m, 200m, 10m }; // abnormal run long ago, most recent fill is fine
        Assert.False(AbnormalSlippageDetector.ShouldTriggerKillSwitch(slippage, abnormalThresholdBps: 100m, consecutiveThreshold: 3));
    }

    [Fact]
    public void AbnormalSlippage_ExactlyAtThreshold_IsNotAbnormal()
    {
        var slippage = new decimal[] { 100m, 100m, 100m };
        Assert.False(AbnormalSlippageDetector.ShouldTriggerKillSwitch(slippage, abnormalThresholdBps: 100m, consecutiveThreshold: 3));
    }
}
