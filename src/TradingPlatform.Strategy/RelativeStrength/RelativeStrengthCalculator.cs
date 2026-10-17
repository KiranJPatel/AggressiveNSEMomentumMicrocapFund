using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Strategy.RelativeStrength;

/// <summary>
/// Computes relative strength of a stock against ONE benchmark, across
/// every configured period (spec §3.3). Pure function: takes two
/// already-aligned close-price series (see PriceSeriesAligner), the most
/// recent value at the end of each list ("today").
///
/// Per period N: raw outperformance is the % change in the Mansfield
/// Relative-Performance ratio (stock/benchmark) over N bars — see
/// RsOptions class remarks for the confirmed formula and why a per-period
/// version is used instead of Mansfield's single 52-week-SMA oscillator.
/// That raw % is then squashed to a 0-100 score via tanh (50 = neutral).
/// </summary>
public static class RelativeStrengthCalculator
{
    public static RelativeStrengthResult Calculate(IReadOnlyList<decimal> stockCloses, IReadOnlyList<decimal> benchmarkCloses, RsOptions options)
    {
        if (stockCloses.Count != benchmarkCloses.Count)
            throw new ArgumentException("Stock and benchmark close series must be the same (already-aligned) length.");

        var periods = new List<PeriodRelativeStrength>();
        var n = stockCloses.Count;

        foreach (var period in options.Periods)
        {
            if (n <= period || stockCloses[n - 1 - period] <= 0 || benchmarkCloses[n - 1 - period] <= 0)
            {
                periods.Add(new PeriodRelativeStrength(period, 0m, 50m)); // not enough history — neutral, not fabricated-positive
                continue;
            }

            var stockReturn = stockCloses[n - 1] / stockCloses[n - 1 - period] - 1m;
            var benchmarkReturn = benchmarkCloses[n - 1] / benchmarkCloses[n - 1 - period] - 1m;

            var rawOutperformancePercent = benchmarkReturn > -1m
                ? ((1m + stockReturn) / (1m + benchmarkReturn) - 1m) * 100m
                : 0m;

            var normalizedScore = NormalizeToScore(rawOutperformancePercent, options.NormalizationScalePercent);
            periods.Add(new PeriodRelativeStrength(period, rawOutperformancePercent, normalizedScore));
        }

        var composite = ComputeComposite(periods, options);
        return new RelativeStrengthResult(periods, composite);
    }

    private static decimal NormalizeToScore(decimal rawOutperformancePercent, decimal normalizationScalePercent) =>
        ScoreNormalization.SquashToScore(rawOutperformancePercent, normalizationScalePercent);

    private static decimal ComputeComposite(IReadOnlyList<PeriodRelativeStrength> periods, RsOptions options)
    {
        if (periods.Count == 0) return 50m;

        decimal weightedSum = 0, totalWeight = 0;
        for (var i = 0; i < periods.Count; i++)
        {
            var weight = i < options.PeriodWeights.Length ? options.PeriodWeights[i] : 0m;
            weightedSum += periods[i].NormalizedScore * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : periods.Average(p => p.NormalizedScore);
    }
}
