using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.MovingAverages;

namespace TradingPlatform.Indicators.Volatility;

/// <summary>
/// Wilder's ATR: first value is a simple average of the first N True Range
/// values; subsequent values use Wilder's smoothing:
///   ATR = [(Prior ATR x (N-1)) + Current TR] / N
/// Confirmed formula (StockCharts.com ChartSchool "Average True Range (ATR)").
/// </summary>
public static class AverageTrueRange
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<HistoricalBar> bars, int period)
    {
        var trueRanges = TrueRange.Calculate(bars);
        return ExponentialMovingAverage.CalculateWilder(trueRanges, period);
    }

    /// <summary>ATR expressed as a percentage of Close (spec §3.4 "ATR%") — for cross-stock volatility comparison.</summary>
    public static IReadOnlyList<decimal?> CalculatePercent(IReadOnlyList<HistoricalBar> bars, int period)
    {
        var atr = Calculate(bars, period);
        var result = new decimal?[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            if (atr[i].HasValue && bars[i].Close != 0)
                result[i] = atr[i]!.Value / bars[i].Close * 100m;
        }
        return result;
    }
}
