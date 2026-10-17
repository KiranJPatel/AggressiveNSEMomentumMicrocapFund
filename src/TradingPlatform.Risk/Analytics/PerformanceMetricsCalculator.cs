namespace TradingPlatform.Risk.Analytics;

/// <summary>
/// CAGR and daily-return Sharpe/Sortino from a NAV time series. This
/// mirrors BacktestEngine's BuildBacktestRun/ComputeSharpeSortino (Tranche
/// 11) formula-for-formula, extracted as a standalone public calculator so
/// GET /api/performance (live/paper trading-to-date metrics, Tranche 18)
/// can reuse the exact same math without depending on BacktestEngine's
/// internals. Deliberately NOT a refactor of BacktestEngine itself to call
/// this instead: that engine is a heavily-tested, load-bearing 600-line
/// file with 613 passing tests behind it and no compiler available in this
/// sandbox to verify a refactor — touching it for a reuse convenience is
/// not a risk worth taking here. Same 0% risk-free-rate simplification as
/// the original (spec doesn't specify one).
/// </summary>
public static class PerformanceMetricsCalculator
{
    public static decimal? ComputeCagr(decimal initialCapital, decimal finalCapital, int totalDays)
    {
        if (totalDays <= 0 || finalCapital <= 0 || initialCapital <= 0) return null;

        var growth = Math.Pow((double)(finalCapital / initialCapital), 365.0 / totalDays) - 1.0;
        return !double.IsNaN(growth) && !double.IsInfinity(growth) ? (decimal)growth * 100m : null;
    }

    public static (decimal? Sharpe, decimal? Sortino) ComputeSharpeSortino(IReadOnlyList<decimal> navSeriesAscending, int tradingDaysPerYear)
    {
        if (navSeriesAscending.Count < 3) return (null, null);

        var returns = new List<decimal>(navSeriesAscending.Count - 1);
        for (var i = 1; i < navSeriesAscending.Count; i++)
            if (navSeriesAscending[i - 1] > 0) returns.Add((navSeriesAscending[i] - navSeriesAscending[i - 1]) / navSeriesAscending[i - 1]);

        if (returns.Count < 2) return (null, null);

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var stdDev = (decimal)Math.Sqrt((double)variance);

        var downside = returns.Where(r => r < 0).ToList();
        decimal? sortino = null;
        if (downside.Count >= 2)
        {
            var downsideVariance = downside.Sum(r => r * r) / (downside.Count - 1);
            var downsideStdDev = (decimal)Math.Sqrt((double)downsideVariance);
            sortino = downsideStdDev > 0 ? mean / downsideStdDev * (decimal)Math.Sqrt(tradingDaysPerYear) : null;
        }

        var sharpe = stdDev > 0 ? mean / stdDev * (decimal)Math.Sqrt(tradingDaysPerYear) : (decimal?)null;
        return (sharpe, sortino);
    }
}
