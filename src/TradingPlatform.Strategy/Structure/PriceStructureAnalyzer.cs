using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Structure;

namespace TradingPlatform.Strategy.Structure;

/// <summary>
/// Reads price structure (spec §3.4) from CONFIRMED swing points only — <see cref="SwingPointDetector"/> never returns a swing that
/// needs bars beyond the series end to confirm, so nothing here looks ahead.
/// Support = the highest confirmed swing LOW below the close; Resistance = the lowest confirmed swing HIGH above it, both within the last
/// <c>windowBars</c> bars so a pivot from years ago is not presented as a live level. A swing exactly at the close is neither.
/// </summary>
public static class PriceStructureAnalyzer
{
    public static PriceStructure Analyze(IReadOnlyList<HistoricalBar> bars, decimal close, int swingLookback, int windowBars = 252)
    {
        if (bars.Count == 0) return new PriceStructure(PriceStructureState.Insufficient, null, null, null, null);

        var swings = SwingPointDetector.Detect(bars, swingLookback);
        var highs = swings.Where(p => p.Type == SwingPointType.High).ToList();
        var lows = swings.Where(p => p.Type == SwingPointType.Low).ToList();

        var lastHighLabel = highs.Count > 0 ? highs[^1].StructureLabel : null;   // null for the very first swing high: nothing to compare with
        var lastLowLabel = lows.Count > 0 ? lows[^1].StructureLabel : null;

        var state = (lastHighLabel, lastLowLabel) switch
        {
            (null, _) or (_, null) => PriceStructureState.Insufficient,
            ("HH", "HL") => PriceStructureState.Uptrend,
            ("LH", "LL") => PriceStructureState.Downtrend,
            _ => PriceStructureState.Mixed,
        };

        var windowStart = Math.Max(0, bars.Count - windowBars);
        decimal? support = lows.Where(p => p.BarIndex >= windowStart && p.Price < close).Select(p => (decimal?)p.Price).DefaultIfEmpty(null).Max();
        decimal? resistance = highs.Where(p => p.BarIndex >= windowStart && p.Price > close).Select(p => (decimal?)p.Price).DefaultIfEmpty(null).Min();

        return new PriceStructure(state, support, resistance, lastHighLabel, lastLowLabel);
    }
}
