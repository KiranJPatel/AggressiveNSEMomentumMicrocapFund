namespace TradingPlatform.Execution.Orchestration;

/// <summary>
/// Spec §6.4 names "repeated order rejection" as a kill-switch source
/// without a threshold — resolved (see LiveTradingOptions.
/// RejectionConsecutiveThreshold's doc) as N CONSECUTIVE rejections, not
/// a rejection rate over some window. Consecutive is the more targeted
/// signal: a system rejecting orders back-to-back right now indicates an
/// active, ongoing problem (a stale price, a broken validation input, a
/// broker-side issue) worth halting for immediately; a rejection rate
/// diluted across a longer history could stay under threshold while a
/// fresh problem is actively happening.
/// </summary>
public static class RepeatedRejectionDetector
{
    /// <summary>recentOutcomes is ordered oldest-first, newest-last — only the trailing run of consecutive `true` (rejected) entries counts.</summary>
    public static bool ShouldTriggerKillSwitch(IReadOnlyList<bool> recentOutcomesOldestFirst, int consecutiveThreshold)
    {
        var consecutive = 0;
        for (var i = recentOutcomesOldestFirst.Count - 1; i >= 0; i--)
        {
            if (!recentOutcomesOldestFirst[i]) break;
            consecutive++;
        }
        return consecutive >= consecutiveThreshold;
    }
}

/// <summary>
/// Spec §6.4 names "abnormal slippage" as a kill-switch source without a
/// threshold — resolved analogously to RepeatedRejectionDetector:
/// consecutive fills exceeding a bps ceiling, not a rolling average,
/// since an average can stay muted by history while a fresh, actively
/// deteriorating execution environment (a liquidity event, a data feed
/// problem making fills look wrong, a broker-side issue) is exactly the
/// kind of thing that should trip a halt without waiting for enough
/// history to average it out.
/// </summary>
public static class AbnormalSlippageDetector
{
    public static bool ShouldTriggerKillSwitch(IReadOnlyList<decimal> recentSlippageBpsOldestFirst, decimal abnormalThresholdBps, int consecutiveThreshold)
    {
        var consecutive = 0;
        for (var i = recentSlippageBpsOldestFirst.Count - 1; i >= 0; i--)
        {
            if (recentSlippageBpsOldestFirst[i] <= abnormalThresholdBps) break;
            consecutive++;
        }
        return consecutive >= consecutiveThreshold;
    }
}
