namespace TradingPlatform.Core.Models;

/// <summary>One scored parameter combination from an <c>optimize</c> run (OptimizationRuns table).</summary>
public sealed class OptimizationRunRecord
{
    public long Id { get; set; }

    /// <summary>Groups every candidate written by the same optimize invocation. Assigned by the repository.</summary>
    public string BatchId { get; set; } = "";

    /// <summary>JSON object of parameter name -> value (the swept dimensions only).</summary>
    public required string ParameterSetJson { get; set; }

    /// <summary>JSON object of the score components (train/validate scores, drawdown, trade counts, robustness, flags) — what the composite was built from.</summary>
    public required string CompositeScoreBreakdownJson { get; set; }

    public decimal CompositeScore { get; set; }

    /// <summary>True when CompositeScore was computed on data the combination was NOT selected on (the Validate slice).</summary>
    public bool IsOutOfSample { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
