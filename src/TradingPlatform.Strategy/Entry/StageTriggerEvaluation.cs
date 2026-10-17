namespace TradingPlatform.Strategy.Entry;

/// <summary>One named condition in a stage-2/3 trigger evaluation — mirrors Breakout.BreakoutCondition's explainable-audit-trail pattern.</summary>
public sealed record StageTriggerCondition(string Name, bool Passed, string Detail);

public sealed record StageTriggerEvaluation(bool ShouldTrigger, IReadOnlyList<StageTriggerCondition> Conditions);
