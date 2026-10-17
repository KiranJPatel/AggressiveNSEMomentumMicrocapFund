using TradingPlatform.Core.Models;

namespace TradingPlatform.Risk.Analytics;

/// <summary>
/// Computes spec §4.4's aggregate trade statistics from a set of completed
/// trades. Pure function, no I/O — a trade journal repository (later
/// tranches) supplies the Trade list.
/// </summary>
public static class TradeAnalyticsCalculator
{
    public static TradeAnalyticsSummary Calculate(IReadOnlyList<Trade> trades)
    {
        if (trades.Count == 0)
            return new TradeAnalyticsSummary(0, null, null, null, null, null, null, null);

        var rMultiples = trades.Select(t => t.RMultiple).OrderBy(r => r).ToList();
        var averageR = rMultiples.Average();
        var medianR = Median(rMultiples);

        var winners = trades.Where(t => t.RealizedPnl > 0).ToList();
        var losers = trades.Where(t => t.RealizedPnl <= 0).ToList();

        var winRatePercent = 100m * winners.Count / trades.Count;
        var averageWinnerR = winners.Count > 0 ? winners.Average(t => t.RMultiple) : (decimal?)null;
        var averageLoserR = losers.Count > 0 ? losers.Average(t => t.RMultiple) : (decimal?)null;

        var grossProfit = winners.Sum(t => t.RealizedPnl);
        var grossLoss = Math.Abs(losers.Sum(t => t.RealizedPnl));
        var profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (decimal?)null; // undefined with zero losses, not "infinite"

        decimal? expectancyR = null;
        if (averageWinnerR is not null || averageLoserR is not null)
        {
            var winRateFraction = winners.Count / (decimal)trades.Count;
            var lossRateFraction = losers.Count / (decimal)trades.Count;
            expectancyR = winRateFraction * (averageWinnerR ?? 0m) + lossRateFraction * (averageLoserR ?? 0m);
        }

        return new TradeAnalyticsSummary(trades.Count, averageR, medianR, winRatePercent, averageWinnerR, averageLoserR, profitFactor, expectancyR);
    }

    private static decimal Median(IReadOnlyList<decimal> sortedValues)
    {
        var n = sortedValues.Count;
        if (n % 2 == 1) return sortedValues[n / 2];
        return (sortedValues[n / 2 - 1] + sortedValues[n / 2]) / 2m;
    }
}
