using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Strategy.Breakout;
using TradingPlatform.Strategy.Entry;

namespace TradingPlatform.Strategy;

/// <summary>
/// THE shared decision core (spec §1.2, §5.6): Backtest, Paper, and Live
/// modes all call this exact function to decide what to do with a symbol
/// today. They differ only in which providers supplied the
/// StrategyStepInputs — never in this logic. Pure function, no I/O.
///
/// Routing: no open position -> only a Stage 1 breakout entry is possible
/// (BreakoutEngine, Tranche 6, gated on a VCP pivot existing at all);
/// Stage 1 filled -> evaluate Stage 2's trigger (Entry.Stage2TriggerEvaluator);
/// Stage 2 filled -> evaluate Stage 3's retest trigger
/// (Entry.Stage3TriggerEvaluator) using the bar window since the Stage 2
/// fill; Stage 3 filled -> fully staged in, nothing left to add (exits are
/// a separate concern, Tranche 9).
/// </summary>
public static class StrategyStepEngine
{
    public static StrategyStepResult Evaluate(StrategyStepInputs inputs, StrategyOptions strategyOptions, RsOptions rsOptions)
    {
        if (inputs.Bars.Count == 0 || inputs.IndicatorSnapshots.Count == 0)
            throw new ArgumentException("StrategyStepEngine requires at least one bar/indicator snapshot.");

        var currentBar = inputs.Bars[^1];
        var currentIndicators = inputs.IndicatorSnapshots[^1];

        return inputs.ExistingPosition switch
        {
            null => EvaluateStage1(inputs, currentBar.Close, currentIndicators, strategyOptions, rsOptions),
            { HighestStageFilled: PyramidStage.Stage1Initial } position => EvaluateStage2(inputs, position, currentBar, currentIndicators, strategyOptions),
            { HighestStageFilled: PyramidStage.Stage2Add } position => EvaluateStage3(inputs, position, currentIndicators, strategyOptions),
            _ => new StrategyStepResult(StrategyStepDecision.HoldExisting, null, null, null,
                new[] { "Position is fully staged in (Stage 3 filled) — no further entries." })
        };
    }

    private static StrategyStepResult EvaluateStage1(StrategyStepInputs inputs, decimal close, Core.Models.IndicatorSnapshot indicators, StrategyOptions strategyOptions, RsOptions rsOptions)
    {
        if (inputs.VcpPattern is null)
        {
            return new StrategyStepResult(StrategyStepDecision.NoSignal, null, null, null,
                new[] { "No VCP pattern currently forming — no pivot to break out from." });
        }

        var evaluation = BreakoutEngine.Evaluate(close, inputs.VcpPattern.PivotPrice, indicators, inputs.RelativeStrengthScore, inputs.Regime, strategyOptions, rsOptions, inputs.Delivery);
        var reasons = evaluation.Conditions.Select(c => c.Detail).ToList();

        if (!evaluation.IsBreakout)
            return new StrategyStepResult(StrategyStepDecision.NoSignal, null, null, null, reasons);

        var quantity = inputs.TotalTargetQuantity is { } total ? StagedEntryCalculator.Split(total, strategyOptions).Stage1Qty : (decimal?)null;
        return new StrategyStepResult(StrategyStepDecision.EnterStage1, PyramidStage.Stage1Initial, quantity, inputs.VcpPattern.PivotPrice, reasons);
    }

    private static StrategyStepResult EvaluateStage2(StrategyStepInputs inputs, StagedPositionState position, Core.Models.HistoricalBar currentBar, Core.Models.IndicatorSnapshot indicators, StrategyOptions strategyOptions)
    {
        var evaluation = Stage2TriggerEvaluator.Evaluate(position, currentBar, indicators, strategyOptions);
        var reasons = evaluation.Conditions.Select(c => c.Detail).ToList();

        if (!evaluation.ShouldTrigger)
            return new StrategyStepResult(StrategyStepDecision.HoldExisting, null, null, null, reasons);

        var quantity = inputs.TotalTargetQuantity is { } total ? StagedEntryCalculator.Split(total, strategyOptions).Stage2Qty : (decimal?)null;
        return new StrategyStepResult(StrategyStepDecision.AddStage2, PyramidStage.Stage2Add, quantity, position.Stage1EntryPrice, reasons);
    }

    private static StrategyStepResult EvaluateStage3(StrategyStepInputs inputs, StagedPositionState position, Core.Models.IndicatorSnapshot indicators, StrategyOptions strategyOptions)
    {
        var barsSinceStage2 = position.Stage2EntryTimestamp is { } stage2Timestamp
            ? inputs.Bars.Where(b => b.Timestamp >= stage2Timestamp).ToList()
            : new List<Core.Models.HistoricalBar>();

        var evaluation = Stage3TriggerEvaluator.Evaluate(position, barsSinceStage2, indicators, strategyOptions);
        var reasons = evaluation.Conditions.Select(c => c.Detail).ToList();

        if (!evaluation.ShouldTrigger)
            return new StrategyStepResult(StrategyStepDecision.HoldExisting, null, null, null, reasons);

        var quantity = inputs.TotalTargetQuantity is { } total ? StagedEntryCalculator.Split(total, strategyOptions).Stage3Qty : (decimal?)null;
        return new StrategyStepResult(StrategyStepDecision.AddStage3, PyramidStage.Stage3Add, quantity, position.Stage2EntryPrice, reasons);
    }
}
