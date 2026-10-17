namespace TradingPlatform.Indicators.MovingAverages;

/// <summary>
/// Simple moving average. Pure function, no I/O. Result is index-aligned
/// with the input: index i is null until i >= period - 1 (not enough
/// lookback yet), matching every other indicator in this project so an
/// IndicatorEngine can zip series together safely.
/// </summary>
public static class SimpleMovingAverage
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));

        var result = new decimal?[values.Count];
        decimal runningSum = 0;

        for (var i = 0; i < values.Count; i++)
        {
            runningSum += values[i];
            if (i >= period) runningSum -= values[i - period];
            result[i] = i >= period - 1 ? runningSum / period : null;
        }

        return result;
    }
}
