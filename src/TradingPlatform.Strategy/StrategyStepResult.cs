using TradingPlatform.Core.Enums;

namespace TradingPlatform.Strategy;

public enum StrategyStepDecision { NoSignal, EnterStage1, AddStage2, AddStage3, HoldExisting }

/// <summary>
/// StrategyStepEngine's output. Reasons always lists the evaluated
/// conditions (matching Breakout/Stage2/Stage3's explainable-audit-trail
/// pattern) so a NoSignal/HoldExisting result says exactly what was
/// missing, not just "no".
/// </summary>
public sealed record StrategyStepResult(
    StrategyStepDecision Decision,
    PyramidStage? Stage,
    decimal? SuggestedQuantity,
    decimal? ReferencePrice,
    IReadOnlyList<string> Reasons);
