using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Statistics;

namespace TradingPlatform.Risk.Correlation;

/// <summary>
/// Computes the aggregate exposure of the "cluster" of existing open
/// positions highly correlated with a candidate trade (spec §4.2, 60-day
/// rolling window): "ten highly-correlated names must not count as ten
/// independent bets." Each existing position's bars are date-aligned
/// against the candidate's (DateAlignedCloses — don't assume positional
/// alignment, align by actual trading date) before computing 60-day-return
/// Pearson correlation (Indicators.Statistics). Positions at/above
/// RiskOptions.CorrelationClusterThreshold contribute their full position
/// value to the cluster exposure, which PositionSizingCalculator then caps
/// like a sector exposure limit.
/// </summary>
public static class CorrelationClusterCalculator
{
    public static decimal CalculateClusterExposure(IReadOnlyList<HistoricalBar> candidateBars, IReadOnlyList<CorrelatedPosition> existingPositions, RiskOptions options)
    {
        decimal clusterExposure = 0;

        foreach (var position in existingPositions)
        {
            var (candidateCloses, positionCloses) = DateAlignedCloses.Align(candidateBars, position.Bars);
            if (candidateCloses.Count < options.CorrelationWindowDays + 1) continue;

            var window = options.CorrelationWindowDays + 1;
            var candidateWindow = TakeLast(candidateCloses, window);
            var positionWindow = TakeLast(positionCloses, window);

            var candidateReturns = PearsonCorrelation.ToReturns(candidateWindow);
            var positionReturns = PearsonCorrelation.ToReturns(positionWindow);
            if (candidateReturns.Count < 2) continue;

            var correlation = PearsonCorrelation.Calculate(candidateReturns, positionReturns);
            if (correlation is { } r && Math.Abs(r) >= options.CorrelationClusterThreshold)
                clusterExposure += position.PositionValue;
        }

        return clusterExposure;
    }

    private static IReadOnlyList<decimal> TakeLast(IReadOnlyList<decimal> values, int count) =>
        values.Count <= count ? values : values.Skip(values.Count - count).ToList();
}
