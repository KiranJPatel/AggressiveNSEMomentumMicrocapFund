using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.RelativeStrength;

/// <summary>
/// Aligns a stock's bars with a benchmark's bars by trading date, dropping
/// any date present in only one series. NSE-listed stocks and NSE indices
/// should share a trading calendar, but this guards against any mismatch
/// (a benchmark data gap, a stock trading halt, etc.) rather than assuming
/// perfect alignment and risking an off-by-one comparison.
/// </summary>
public static class PriceSeriesAligner
{
    public static (IReadOnlyList<decimal> StockCloses, IReadOnlyList<decimal> BenchmarkCloses) AlignByDate(
        IReadOnlyList<HistoricalBar> stockBars, IReadOnlyList<HistoricalBar> benchmarkBars)
    {
        // Fast path (the backtest calls this per symbol per day): when BOTH series are strictly ascending by date — the normal case — a
        // linear merge yields exactly the pairs the dictionary path below would, without allocating a dictionary or re-sorting every call.
        // Anything else (unsorted input, a repeated date) takes the original path unchanged, including its exception on duplicate benchmark dates.
        if (IsStrictlyAscendingByDate(stockBars) && IsStrictlyAscendingByDate(benchmarkBars))
            return MergeSorted(stockBars, benchmarkBars);

        var benchmarkByDate = benchmarkBars.ToDictionary(b => DateOnly.FromDateTime(b.Timestamp.DateTime), b => b.Close);

        var stockCloses = new List<decimal>();
        var benchmarkCloses = new List<decimal>();

        foreach (var bar in stockBars.OrderBy(b => b.Timestamp))
        {
            var date = DateOnly.FromDateTime(bar.Timestamp.DateTime);
            if (benchmarkByDate.TryGetValue(date, out var benchmarkClose))
            {
                stockCloses.Add(bar.Close);
                benchmarkCloses.Add(benchmarkClose);
            }
        }

        return (stockCloses, benchmarkCloses);
    }

    private static bool IsStrictlyAscendingByDate(IReadOnlyList<HistoricalBar> bars)
    {
        for (var i = 1; i < bars.Count; i++)
            if (DateOnly.FromDateTime(bars[i].Timestamp.DateTime) <= DateOnly.FromDateTime(bars[i - 1].Timestamp.DateTime)) return false;
        return true;
    }

    private static (IReadOnlyList<decimal>, IReadOnlyList<decimal>) MergeSorted(IReadOnlyList<HistoricalBar> stock, IReadOnlyList<HistoricalBar> benchmark)
    {
        var stockCloses = new List<decimal>(Math.Min(stock.Count, benchmark.Count));
        var benchmarkCloses = new List<decimal>(stockCloses.Capacity);
        int i = 0, j = 0;
        while (i < stock.Count && j < benchmark.Count)
        {
            var sd = DateOnly.FromDateTime(stock[i].Timestamp.DateTime);
            var bd = DateOnly.FromDateTime(benchmark[j].Timestamp.DateTime);
            if (sd == bd) { stockCloses.Add(stock[i].Close); benchmarkCloses.Add(benchmark[j].Close); i++; j++; }
            else if (sd < bd) i++;
            else j++;
        }
        return (stockCloses, benchmarkCloses);
    }
}
