using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// "Trend Structure" component (spec §3.2, 5 pts): how many of the stock's
/// own trend-health sub-signals currently agree. Mirrors
/// MarketRegimeEngine.ComputeTrendScore's "count of bullish signals"
/// approach, but per-stock and using the richer indicator set spec §3.4
/// actually computes (HMA, Supertrend, SMA, ADX) rather than just SMA50/
/// SMA200. Only sub-signals whose underlying indicator value is actually
/// available (non-null) are counted, and the score is the fraction of
/// AVAILABLE signals that are bullish — renormalized over what's known
/// rather than penalizing a young listing that simply doesn't have 200
/// bars of history yet for a null-vs-bearish distinction that isn't real.
/// Close &gt; Supertrend stands in for "Supertrend is in an uptrend": in
/// Tranche 3's algorithm the Supertrend line sits below price in an
/// uptrend (it equals the active lower band) and above price in a
/// downtrend (the active upper band) — so this comparison is equivalent to
/// the IsUpTrend flag without needing to persist a second column for it.
/// </summary>
public static class TrendStructureCalculator
{
    public static decimal Calculate(decimal close, IndicatorSnapshot indicators, StrategyOptions strategyOptions)
    {
        var signals = new List<bool>();

        if (indicators.HmaFast is { } hmaFast) signals.Add(close > hmaFast);
        if (indicators.HmaFast is { } hf && indicators.HmaSlow is { } hs) signals.Add(hf > hs);
        if (indicators.Sma50 is { } sma50) signals.Add(close > sma50);
        if (indicators.Sma50 is { } s50 && indicators.Sma200 is { } s200) signals.Add(s50 > s200);
        if (indicators.Supertrend is { } supertrend) signals.Add(close > supertrend);
        if (indicators.Adx is { } adx) signals.Add(adx >= strategyOptions.BreakoutMinAdx);

        // Nothing available: the structure is UNKNOWN, so it earns no credit (spec §0.4). It used to return a neutral 50 — half the
        // component's points handed to a symbol with no indicator history at all.
        if (signals.Count == 0) return 0m;
        return 100m * signals.Count(s => s) / signals.Count;
    }
}
