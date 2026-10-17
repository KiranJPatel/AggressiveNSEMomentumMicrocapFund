using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Entry;

/// <summary>
/// Stage 2 add trigger (spec §3.7): "+2-3% favorable move, trend still
/// valid, no stop violation" — measured from Stage 1's own entry price,
/// not the position's blended average (irrelevant with only one fill so
/// far anyway, but matters conceptually for Stage 3).
///
/// The 2-3% window's operative trigger is the MIN threshold (>=), not a
/// [Min,Max] band: a stronger move (say +6%) still means the position is
/// working — gating pyramiding OFF above some upper ceiling would be
/// perverse for a system whose entire premise is adding to strength. Max
/// documents the typical range the spec describes, not a blocking ceiling
/// (a resolved ambiguity, spec §0.4).
/// </summary>
public static class Stage2TriggerEvaluator
{
    public static StageTriggerEvaluation Evaluate(StagedPositionState position, HistoricalBar currentBar, IndicatorSnapshot currentIndicators, StrategyOptions options)
    {
        if (position.HighestStageFilled != PyramidStage.Stage1Initial)
        {
            return new StageTriggerEvaluation(false, new[]
            {
                new StageTriggerCondition("AlreadyBeyondStage1", false, $"HighestStageFilled is {position.HighestStageFilled}, not Stage1Initial — Stage 2 either already filled or not yet eligible.")
            });
        }

        var movePercent = position.Stage1EntryPrice > 0
            ? (currentBar.Close - position.Stage1EntryPrice) / position.Stage1EntryPrice * 100m
            : 0m;

        var conditions = new List<StageTriggerCondition>
        {
            new("FavorableMove",
                movePercent >= options.Stage2TriggerFavorableMoveMinPercent,
                $"Move since Stage1 entry {movePercent:F2}% {(movePercent >= options.Stage2TriggerFavorableMoveMinPercent ? ">=" : "<")} required {options.Stage2TriggerFavorableMoveMinPercent:F2}%"),

            EvaluateTrendValid(currentIndicators),

            new("NoStopViolation",
                currentBar.Low > position.CurrentStop,
                $"Bar low {currentBar.Low} {(currentBar.Low > position.CurrentStop ? ">" : "<=")} stop {position.CurrentStop}"),

            new("NeverAddToLoser",
                currentBar.Close > position.Stage1EntryPrice,
                $"Close {currentBar.Close} {(currentBar.Close > position.Stage1EntryPrice ? ">" : "<=")} Stage1 entry {position.Stage1EntryPrice}"),
        };

        return new StageTriggerEvaluation(conditions.All(c => c.Passed), conditions);
    }

    private static StageTriggerCondition EvaluateTrendValid(IndicatorSnapshot indicators)
    {
        if (indicators.HmaFast is null || indicators.HmaSlow is null)
            return new StageTriggerCondition("TrendStillValid", false, "HMA fast/slow unavailable — cannot confirm, fails closed.");

        var valid = indicators.HmaFast.Value > indicators.HmaSlow.Value;
        return new StageTriggerCondition("TrendStillValid", valid, $"HmaFast {indicators.HmaFast.Value:F2} {(valid ? ">" : "<=")} HmaSlow {indicators.HmaSlow.Value:F2}");
    }
}
