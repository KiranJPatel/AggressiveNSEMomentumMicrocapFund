using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;

namespace TradingPlatform.Strategy.Scoring;

public sealed record FundamentalAccelerationResult(decimal Score, bool IsDataAvailable);

/// <summary>
/// Shared logic for "Earnings Acceleration" and "Revenue Acceleration"
/// (spec §3.2, 15+10 pts): whether the growth RATE itself is increasing,
/// which genuinely requires two data points (current and prior period),
/// not just a single current level. If either snapshot's growth figure is
/// unavailable, the whole component is Unavailable — never approximated
/// from just the current level, since "acceleration" specifically means
/// the delta, and answering a different (level-only) question under the
/// same label would be misleading, not merely incomplete (spec §3.2: never
/// silently default a missing component to a positive score).
/// </summary>
public static class FundamentalAccelerationCalculator
{
    public static FundamentalAccelerationResult Calculate(
        decimal? currentGrowthPercent, DataAvailability currentAvailability,
        decimal? priorGrowthPercent, DataAvailability priorAvailability,
        StockScoringOptions options)
    {
        if (currentAvailability != DataAvailability.Available || currentGrowthPercent is null ||
            priorAvailability != DataAvailability.Available || priorGrowthPercent is null)
        {
            return new FundamentalAccelerationResult(0m, false);
        }

        var acceleration = currentGrowthPercent.Value - priorGrowthPercent.Value;
        var score = ScoreNormalization.SquashToScore(acceleration, options.FundamentalAccelerationNormalizationScalePoints);
        return new FundamentalAccelerationResult(score, true);
    }
}
