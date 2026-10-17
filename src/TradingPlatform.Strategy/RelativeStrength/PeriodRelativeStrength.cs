namespace TradingPlatform.Strategy.RelativeStrength;

/// <summary>One period's relative-strength reading (spec §3.3: RS20/RS50/RS90/RS180).</summary>
public sealed record PeriodRelativeStrength(int Period, decimal RawOutperformancePercent, decimal NormalizedScore);

/// <summary>Full multi-period relative-strength result against a single benchmark.</summary>
public sealed record RelativeStrengthResult(IReadOnlyList<PeriodRelativeStrength> Periods, decimal CompositeScore);
