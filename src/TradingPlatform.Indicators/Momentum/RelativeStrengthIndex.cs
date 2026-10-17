namespace TradingPlatform.Indicators.Momentum;

/// <summary>
/// Wilder's RSI (New Concepts in Technical Trading Systems, 1978).
/// Confirmed formula (StockCharts.com ChartSchool):
///   First Average Gain = Sum of Gains over the past N periods / N
///   First Average Loss = Sum of Losses over the past N periods / N
///   Average Gain = [(previous Average Gain) x (N-1) + current Gain] / N
///   Average Loss = [(previous Average Loss) x (N-1) + current Loss] / N
///   RS = Average Gain / Average Loss
///   RSI = 100 - (100 / (1 + RS))
/// Special case per the same source: if Average Loss is 0, RSI = 100
/// (avoids division by zero rather than producing an undefined/infinite RS).
/// </summary>
public static class RelativeStrengthIndex
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> closes, int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));

        var result = new decimal?[closes.Count];
        if (closes.Count <= period) return result;

        var gains = new decimal[closes.Count];
        var losses = new decimal[closes.Count];
        for (var i = 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains[i] = change > 0 ? change : 0;
            losses[i] = change < 0 ? -change : 0;
        }

        decimal avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            avgGain += gains[i];
            avgLoss += losses[i];
        }
        avgGain /= period;
        avgLoss /= period;

        result[period] = ComputeRsi(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
            result[i] = ComputeRsi(avgGain, avgLoss);
        }

        return result;
    }

    private static decimal ComputeRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }
}
