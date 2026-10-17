using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Entry;

/// <summary>
/// Stage 3 add trigger (spec §3.7): "after successful breakout retest,
/// support holds, momentum constructive" — a genuinely different, richer
/// pattern than Stage 2's simple percentage move, operationalized here as
/// five concrete conditions over the bars since Stage 2's entry (not just
/// the current bar, since a "retest" is inherently a multi-bar shape: a
/// pullback followed by a recovery):
///
///  1. RetestOccurred — price pulled back at least
///     Stage3MinRetestPullbackPercent from its post-Stage2 high. Without
///     this, "successful retest" wouldn't mean anything — straight-line
///     continuation with no pullback at all isn't a retest of anything.
///  2. SupportHeld — the pullback's low never breached the current stop.
///  3. SuccessfulReclaim — the current close has recovered back above
///     Stage 2's own entry price (the level being "retested" is
///     specifically that fill, the most recent addition to the position).
///  4. MomentumConstructive — ADX still at/above the same strength bar
///     Breakout uses, and price still above the fast HMA.
///  5. NeverAddToLoser — current close still above Stage 2's entry price
///     (spec §3.7: "Never pyramid into a loser. Never average down.").
/// </summary>
public static class Stage3TriggerEvaluator
{
    public static StageTriggerEvaluation Evaluate(
        StagedPositionState position,
        IReadOnlyList<HistoricalBar> barsSinceStage2Entry,
        IndicatorSnapshot currentIndicators,
        StrategyOptions options)
    {
        if (position.HighestStageFilled != PyramidStage.Stage2Add || position.Stage2EntryPrice is not { } stage2Entry)
        {
            return new StageTriggerEvaluation(false, new[]
            {
                new StageTriggerCondition("AlreadyBeyondOrBeforeStage2", false, $"HighestStageFilled is {position.HighestStageFilled} — Stage 3 requires Stage 2 to already be filled.")
            });
        }

        if (barsSinceStage2Entry.Count == 0)
        {
            return new StageTriggerEvaluation(false, new[]
            {
                new StageTriggerCondition("NoBarsSinceStage2", false, "No bars supplied since the Stage 2 fill — cannot evaluate a retest.")
            });
        }

        // The pullback must be measured FROM the peak onward, not as
        // (window max - window min) — a low that occurs BEFORE the peak
        // isn't a retest of anything, it's just where the rally started.
        // Using the whole-window minimum would falsely flag straight-line
        // continuation (peak forms late, starting low is coincidentally
        // the window minimum) as if it were a genuine pullback-then-recovery.
        var peakIndex = 0;
        for (var i = 1; i < barsSinceStage2Entry.Count; i++)
        {
            if (barsSinceStage2Entry[i].High > barsSinceStage2Entry[peakIndex].High) peakIndex = i;
        }

        var postStage2High = barsSinceStage2Entry[peakIndex].High;
        var postPeakLow = barsSinceStage2Entry.Skip(peakIndex).Min(b => b.Low);
        var currentClose = barsSinceStage2Entry[^1].Close;

        var pullbackPercent = postStage2High > 0 ? (postStage2High - postPeakLow) / postStage2High * 100m : 0m;

        var conditions = new List<StageTriggerCondition>
        {
            new("RetestOccurred",
                pullbackPercent >= options.Stage3MinRetestPullbackPercent,
                $"Pullback since post-Stage2 high {pullbackPercent:F2}% {(pullbackPercent >= options.Stage3MinRetestPullbackPercent ? ">=" : "<")} required {options.Stage3MinRetestPullbackPercent:F2}%"),

            new("SupportHeld",
                postPeakLow > position.CurrentStop,
                $"Lowest low since the post-Stage2 high {postPeakLow} {(postPeakLow > position.CurrentStop ? ">" : "<=")} stop {position.CurrentStop}"),

            new("SuccessfulReclaim",
                currentClose > stage2Entry,
                $"Close {currentClose} {(currentClose > stage2Entry ? ">" : "<=")} Stage2 entry {stage2Entry}"),

            EvaluateMomentumConstructive(currentClose, currentIndicators, options),

            new("NeverAddToLoser",
                currentClose > stage2Entry,
                $"Close {currentClose} {(currentClose > stage2Entry ? ">" : "<=")} Stage2 entry {stage2Entry}"),
        };

        return new StageTriggerEvaluation(conditions.All(c => c.Passed), conditions);
    }

    private static StageTriggerCondition EvaluateMomentumConstructive(decimal close, IndicatorSnapshot indicators, StrategyOptions options)
    {
        if (indicators.Adx is null || indicators.HmaFast is null)
            return new StageTriggerCondition("MomentumConstructive", false, "ADX/HmaFast unavailable — cannot confirm, fails closed.");

        var constructive = indicators.Adx.Value >= options.BreakoutMinAdx && close > indicators.HmaFast.Value;
        return new StageTriggerCondition("MomentumConstructive", constructive,
            $"Adx={indicators.Adx.Value:F2} (need >= {options.BreakoutMinAdx:F2}), Close {(close > indicators.HmaFast.Value ? ">" : "<=")} HmaFast {indicators.HmaFast.Value:F2}");
    }
}
