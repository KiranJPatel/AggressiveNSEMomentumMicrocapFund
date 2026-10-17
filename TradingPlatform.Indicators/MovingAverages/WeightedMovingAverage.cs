namespace TradingPlatform.Indicators.MovingAverages;

/// <summary>
/// Linearly weighted moving average: the most recent value in the window
/// gets weight = period, the oldest gets weight 1, normalized by the sum
/// of weights (period*(period+1)/2). Building block for HullMovingAverage.
/// </summary>
public static class WeightedMovingAverage
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));

        var result = new decimal?[values.Count];
        var weightSum = period * (period + 1) / 2m;

        for (var i = 0; i < values.Count; i++)
        {
            if (i < period - 1) { result[i] = null; continue; }

            decimal weightedSum = 0;
            for (var w = 0; w < period; w++)
            {
                // Most recent (index i) gets the highest weight (period); oldest in the window gets weight 1.
                weightedSum += values[i - w] * (period - w);
            }

            result[i] = weightedSum / weightSum;
        }

        return result;
    }
}
