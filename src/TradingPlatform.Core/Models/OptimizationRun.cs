namespace TradingPlatform.Core.Models;

/// <summary>A parameter-optimization / walk-forward run (spec §5.5).</summary>
public sealed class OptimizationRun
{
    public long Id { get; set; }
    public required string ParameterSetJson { get; set; }
    public required string CompositeScoreBreakdownJson { get; set; }
    public decimal CompositeScore { get; set; }
    public bool IsOutOfSample { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
