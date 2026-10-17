namespace TradingPlatform.Strategy.Breakout;

/// <summary>One named condition from the spec §3.6 breakout formula, with its pass/fail outcome and a human-readable detail — an explainable audit trail, not just a bool.</summary>
public sealed record BreakoutCondition(string Name, bool Passed, string Detail);

/// <summary>Full breakout evaluation: signal fires only if every condition passed.</summary>
public sealed record BreakoutEvaluation(bool IsBreakout, IReadOnlyList<BreakoutCondition> Conditions);
