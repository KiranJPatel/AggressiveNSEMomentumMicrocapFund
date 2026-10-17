using TradingPlatform.Core.Models;

namespace TradingPlatform.Scanner.Universe;

/// <summary>Spec §2.4: "Average Daily Turnover, 20D avg volume, 20D avg traded value, median traded value."</summary>
public sealed record LiquidityMetrics(decimal AverageDailyVolume, decimal AverageDailyTradedValue, decimal MedianDailyTradedValue);

public static class LiquidityMetricsCalculator
{
    public static LiquidityMetrics Calculate(IReadOnlyList<HistoricalBar> bars, int lookbackDays)
    {
        var window = bars.TakeLast(lookbackDays).ToList();
        if (window.Count == 0) return new LiquidityMetrics(0m, 0m, 0m);

        var tradedValues = window.Select(b => b.Close * b.Volume).OrderBy(v => v).ToList();
        var averageVolume = window.Average(b => (decimal)b.Volume);
        var averageTradedValue = tradedValues.Average();
        var median = tradedValues.Count % 2 == 1
            ? tradedValues[tradedValues.Count / 2]
            : (tradedValues[tradedValues.Count / 2 - 1] + tradedValues[tradedValues.Count / 2]) / 2m;

        return new LiquidityMetrics(averageVolume, averageTradedValue, median);
    }
}
