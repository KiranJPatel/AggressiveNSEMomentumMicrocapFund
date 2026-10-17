using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Resampling;

namespace TradingPlatform.Strategy.Regime;

/// <summary>
/// Weekly-timeframe market bias of the broad index (spec §7.3: weekly = market bias, daily = stock setup). Builds weekly bars from the
/// daily index series (point-in-time: only bars given, the forming week reflects data up to the last bar) and counts three signals:
///   1. weekly close above its fast SMA            2. fast SMA above slow SMA            3. fast SMA higher than N weeks ago.
/// 3 agree = Bullish, 2 = Neutral, 0-1 = Bearish. With too few weekly bars for the slow SMA plus the slope lookback the bias is Unknown —
/// never defaulted to Bullish or Neutral.
/// </summary>
public static class WeeklyMarketBiasCalculator
{
    public static WeeklyBias Calculate(IReadOnlyList<HistoricalBar> dailyBroadIndexBars, MarketRegimeOptions options)
    {
        var fast = Math.Max(2, options.WeeklyFastPeriod);
        var slow = Math.Max(fast + 1, options.WeeklySlowPeriod);
        var lookback = Math.Max(1, options.WeeklySlopeLookbackWeeks);

        // Only the last (slow + lookback) weeks can influence the result. The regime is recomputed every simulated day, so resampling the whole
        // multi-year index series each time was wasted work: keep the tail covering those weeks plus a margin (so a partial first week of the
        // window is never one of the weeks used).
        var weekly = BarResampler.ToWeekly(Tail(dailyBroadIndexBars, (slow + lookback + 3) * 7));
        if (weekly.Count < slow + lookback) return WeeklyBias.Unknown;

        decimal Sma(int endExclusive, int period)
        {
            decimal sum = 0m;
            for (var i = endExclusive - period; i < endExclusive; i++) sum += weekly[i].Close;
            return sum / period;
        }

        var n = weekly.Count;
        var smaFast = Sma(n, fast);
        var smaSlow = Sma(n, slow);
        var smaFastBefore = Sma(n - lookback, fast);

        var bullish = 0;
        if (weekly[^1].Close > smaFast) bullish++;
        if (smaFast > smaSlow) bullish++;
        if (smaFast > smaFastBefore) bullish++;

        return bullish switch { 3 => WeeklyBias.Bullish, 2 => WeeklyBias.Neutral, _ => WeeklyBias.Bearish };
    }

    /// <summary>The bars within <paramref name="calendarDays"/> of the last bar (input assumed ascending, as all cached series are; unsorted input is returned whole).</summary>
    private static IReadOnlyList<HistoricalBar> Tail(IReadOnlyList<HistoricalBar> bars, int calendarDays)
    {
        if (bars.Count == 0) return bars;
        for (var i = 1; i < bars.Count; i++) if (bars[i].Timestamp < bars[i - 1].Timestamp) return bars;
        var cutoff = bars[^1].Timestamp.AddDays(-calendarDays);
        var start = bars.Count;
        while (start > 0 && bars[start - 1].Timestamp >= cutoff) start--;
        if (start == 0) return bars;
        var tail = new List<HistoricalBar>(bars.Count - start);
        for (var i = start; i < bars.Count; i++) tail.Add(bars[i]);
        return tail;
    }
}
