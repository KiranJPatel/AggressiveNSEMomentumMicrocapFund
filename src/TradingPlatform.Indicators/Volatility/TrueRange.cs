using TradingPlatform.Core.Models;

namespace TradingPlatform.Indicators.Volatility;

/// <summary>
/// True Range (Wilder): max(High-Low, |High-PrevClose|, |Low-PrevClose|).
/// Confirmed formula (Wikipedia "Average true range", StockCharts ChartSchool).
/// The very first bar has no previous close, so TR[0] = High[0] - Low[0].
/// </summary>
public static class TrueRange
{
    public static IReadOnlyList<decimal> Calculate(IReadOnlyList<HistoricalBar> bars)
    {
        var result = new decimal[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            if (i == 0)
            {
                result[i] = bars[i].High - bars[i].Low;
                continue;
            }

            var prevClose = bars[i - 1].Close;
            result[i] = Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - prevClose), Math.Abs(bars[i].Low - prevClose)));
        }
        return result;
    }
}
