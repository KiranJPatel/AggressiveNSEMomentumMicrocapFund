namespace TradingPlatform.Indicators.MovingAverages;

/// <summary>
/// Exponential moving average, seeded with a simple average of the first
/// `period` values (the standard convention — see e.g. StockCharts.com's
/// EMA methodology), then recurred with the standard smoothing factor
/// alpha = 2 / (period + 1).
/// </summary>
public static class ExponentialMovingAverage
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));

        var result = new decimal?[values.Count];
        if (values.Count < period) return result;

        var alpha = 2m / (period + 1);

        decimal seed = 0;
        for (var i = 0; i < period; i++) seed += values[i];
        seed /= period;
        result[period - 1] = seed;

        var prev = seed;
        for (var i = period; i < values.Count; i++)
        {
            var current = (values[i] - prev) * alpha + prev;
            result[i] = current;
            prev = current;
        }

        return result;
    }

    /// <summary>
    /// Wilder's smoothing: mathematically an EMA with alpha = 1/period
    /// instead of 2/(period+1). Used by ATR, RSI's average gain/loss, and
    /// ADX's directional-movement smoothing — all three of Wilder's
    /// original indicators share this exact recurrence.
    /// </summary>
    public static IReadOnlyList<decimal?> CalculateWilder(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));

        var result = new decimal?[values.Count];
        if (values.Count < period) return result;

        decimal seed = 0;
        for (var i = 0; i < period; i++) seed += values[i];
        seed /= period;
        result[period - 1] = seed;

        var prev = seed;
        for (var i = period; i < values.Count; i++)
        {
            var current = (prev * (period - 1) + values[i]) / period;
            result[i] = current;
            prev = current;
        }

        return result;
    }
}
