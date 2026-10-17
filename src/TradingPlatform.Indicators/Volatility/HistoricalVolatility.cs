namespace TradingPlatform.Indicators.Volatility;

/// <summary>
/// Annualized historical volatility: the rolling-window sample standard
/// deviation of daily log returns, annualized by sqrt(tradingDaysPerYear)
/// and expressed as a percentage. Standard, widely-used definition (e.g.
/// used identically by CBOE-style realized-vol calculations).
/// </summary>
public static class HistoricalVolatility
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> closes, int period, int tradingDaysPerYear)
    {
        if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 2 to compute a standard deviation.");

        var n = closes.Count;
        var logReturns = new double?[n];
        for (var i = 1; i < n; i++)
        {
            if (closes[i - 1] > 0 && closes[i] > 0)
                logReturns[i] = Math.Log((double)(closes[i] / closes[i - 1]));
        }

        var result = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (i < period) continue;

            var window = new List<double>(period);
            var windowValid = true;
            for (var w = i - period + 1; w <= i; w++)
            {
                if (!logReturns[w].HasValue) { windowValid = false; break; }
                window.Add(logReturns[w]!.Value);
            }
            if (!windowValid) continue;

            var mean = window.Average();
            var sumSquaredDeviations = window.Sum(r => (r - mean) * (r - mean));
            var sampleStdDev = Math.Sqrt(sumSquaredDeviations / (window.Count - 1));
            var annualized = sampleStdDev * Math.Sqrt(tradingDaysPerYear) * 100.0;

            result[i] = (decimal)annualized;
        }

        return result;
    }
}
