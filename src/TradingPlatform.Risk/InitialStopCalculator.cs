using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Structure;
using TradingPlatform.Indicators.Volatility;

namespace TradingPlatform.Risk;

/// <summary>
/// Initial stop placement (spec §4.2):
///   InitialStop = min(technical swing-low invalidation, 2.0-2.5 × ATR)
/// Taking the numeric MIN of two candidate stop PRICES means picking
/// whichever is further below entry — the wider, more conservative stop
/// that gives the trade more room before ordinary noise stops it out
/// (which is the plain reading of "min" applied to prices, not distances).
/// "2.0-2.5×ATR" is a configured range (RiskOptions.StopAtrMultiplierMin/
/// Max, Tranche 0); the operative multiplier used here is
/// StopAtrMultiplierMin — the "configurable selection rule" the spec
/// leaves open is which single value within that range to actually apply,
/// resolved here as the range's own minimum (tightest ATR-based stop),
/// documented rather than silently picked.
///
/// Technical swing-low reuses Tranche 3's SwingPointDetector (already
/// no-look-ahead) rather than a parallel implementation — the most recent
/// CONFIRMED swing low at or before the entry bar.
/// </summary>
public static class InitialStopCalculator
{
    public static decimal Calculate(IReadOnlyList<HistoricalBar> bars, int entryBarIndex, decimal entryPrice, RiskOptions options)
    {
        if (entryBarIndex < 0 || entryBarIndex >= bars.Count)
            throw new ArgumentOutOfRangeException(nameof(entryBarIndex));

        var atr = AverageTrueRange.Calculate(bars, options.AtrPeriodForStop);
        var atrValue = atr[entryBarIndex];

        var barsUpToEntry = bars.Take(entryBarIndex + 1).ToList();
        var swingPoints = SwingPointDetector.Detect(barsUpToEntry, options.StopSwingLookback);
        var mostRecentSwingLow = swingPoints
            .Where(p => p.Type == SwingPointType.Low && p.BarIndex <= entryBarIndex)
            .OrderByDescending(p => p.BarIndex)
            .FirstOrDefault();

        if (atrValue is null && mostRecentSwingLow is null)
            throw new InvalidOperationException("Cannot compute an initial stop: neither ATR nor a confirmed swing low is available (insufficient history).");

        var atrBasedStop = atrValue is { } atrVal ? entryPrice - options.StopAtrMultiplierMin * atrVal : (decimal?)null;
        var technicalStop = mostRecentSwingLow?.Price;

        // Fall back to whichever single method is actually available rather than requiring both (a young listing may not have a confirmed swing low yet within the window).
        if (technicalStop is null) return atrBasedStop!.Value;
        if (atrBasedStop is null) return technicalStop.Value;

        return Math.Min(technicalStop.Value, atrBasedStop.Value);
    }
}
