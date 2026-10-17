using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// "Price Momentum" component (spec §3.2, 15 pts): raw absolute return over
/// a lookback period — deliberately NOT benchmark-relative (that's the
/// separate "Relative Strength" component, 20 pts, spec §3.3). A stock can
/// have strong absolute momentum while merely matching a roaring market,
/// or weak absolute momentum while still beating a falling one; scoring
/// both separately (rather than only relative strength) keeps that
/// distinction visible instead of collapsing it into one number.
/// </summary>
public static class PriceMomentumCalculator
{
    public static decimal Calculate(IReadOnlyList<HistoricalBar> bars, StockScoringOptions options)
    {
        var n = bars.Count;
        var period = options.PriceMomentumPeriodBars;
        if (n <= period || bars[n - 1 - period].Close <= 0) return 50m;

        var returnPercent = (bars[n - 1].Close / bars[n - 1 - period].Close - 1m) * 100m;
        return ScoreNormalization.SquashToScore(returnPercent, options.PriceMomentumNormalizationScalePercent);
    }
}
