using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Regime;

/// <summary>One symbol's state on the as-of date. Sma50/Sma200 are null until that symbol has enough history; PreviousClose is null on its first bar.</summary>
public readonly record struct BreadthObservation(decimal Close, decimal? PreviousClose, decimal? Sma50, decimal? Sma200)
{
    /// <summary>
    /// Observation from a symbol's bar history, or null when its last bar is not on <paramref name="asOf"/> — a halted/stale symbol's old
    /// close must not be counted as today's market state. The SMAs are plain means of the last 50/200 closes (null with fewer bars).
    /// </summary>
    public static BreadthObservation? FromBars(IReadOnlyList<HistoricalBar> bars, DateOnly asOf)
    {
        if (bars.Count == 0) return null;
        var last = bars[^1];
        if (DateOnly.FromDateTime(last.Timestamp.Date) != asOf) return null;
        return new BreadthObservation(last.Close, bars.Count > 1 ? bars[^2].Close : null, TailMean(bars, 50), TailMean(bars, 200));
    }

    private static decimal? TailMean(IReadOnlyList<HistoricalBar> bars, int period)
    {
        if (bars.Count < period) return null;
        decimal sum = 0;
        for (var i = bars.Count - period; i < bars.Count; i++) sum += bars[i].Close;
        return sum / period;
    }
}

/// <summary>
/// Universe-wide market-health statistics for the regime engine (spec §3.1: breadth, advance/decline, % of universe above 50/200 DMA).
/// Any member is null when it cannot be computed honestly — the engine then drops that component's weight instead of inventing a value.
/// </summary>
public sealed record UniverseBreadth(decimal? PercentAbove50Dma, decimal? PercentAbove200Dma, decimal? AdvanceDeclineRatio, int AdvancingCount, int DecliningCount, int SampleSize);

public static class UniverseBreadthCalculator
{
    /// <summary>
    /// Percent above each DMA is measured only over symbols that HAVE that DMA (a stock listed 60 days ago has no 200DMA and is not
    /// counted as "below" it). Advance/decline = (advancers + 1) / (decliners + 1): the +1 smoothing keeps the ratio finite when one
    /// side is empty and is neutral (1.0) for 0 vs 0; unchanged closes count for neither side.
    /// </summary>
    public static UniverseBreadth Calculate(IEnumerable<BreadthObservation> observations, int minimumSampleSize)
    {
        int with50 = 0, above50 = 0, with200 = 0, above200 = 0, advancing = 0, declining = 0, withPrev = 0;
        foreach (var o in observations)
        {
            if (o.Sma50 is { } s50) { with50++; if (o.Close > s50) above50++; }
            if (o.Sma200 is { } s200) { with200++; if (o.Close > s200) above200++; }
            if (o.PreviousClose is { } prev)
            {
                withPrev++;
                if (o.Close > prev) advancing++;
                else if (o.Close < prev) declining++;
            }
        }

        var floor = Math.Max(1, minimumSampleSize);
        decimal? p50 = with50 >= floor ? 100m * above50 / with50 : null;
        decimal? p200 = with200 >= floor ? 100m * above200 / with200 : null;
        decimal? ad = withPrev >= floor ? (advancing + 1m) / (declining + 1m) : null;
        return new UniverseBreadth(p50, p200, ad, advancing, declining, withPrev);
    }
}

public static class BreakoutSuccessRateCalculator
{
    /// <summary>
    /// Percent of trades closed in the lookback window (strictly before <paramref name="asOf"/>, so nothing from the decision day leaks in)
    /// that made money net of costs. Null when fewer than the minimum number closed in the window.
    /// </summary>
    public static decimal? Calculate(IEnumerable<Trade> closedTrades, DateOnly asOf, MarketRegimeOptions options)
    {
        var windowStart = asOf.AddDays(-Math.Max(1, options.BreakoutSuccessLookbackDays));
        int total = 0, winners = 0;
        foreach (var t in closedTrades)
        {
            var exitDate = DateOnly.FromDateTime(t.ExitAt.UtcDateTime);
            if (exitDate >= asOf || exitDate < windowStart) continue;
            total++;
            if (t.RealizedPnl > 0m) winners++;
        }
        return total >= Math.Max(1, options.BreakoutSuccessMinimumTrades) ? 100m * winners / total : null;
    }
}
