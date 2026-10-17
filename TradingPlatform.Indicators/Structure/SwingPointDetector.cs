using TradingPlatform.Core.Models;

namespace TradingPlatform.Indicators.Structure;

public enum SwingPointType { High, Low }

/// <summary>
/// A confirmed swing point (spec §3.4 "price structure"). StructureLabel is
/// HH/LH (for Highs) or HL/LL (for Lows) relative to the previous swing
/// point of the same type — null for the first swing point of its type in
/// the series, since there's nothing to compare against yet.
/// </summary>
public sealed record SwingPoint(int BarIndex, DateTimeOffset Timestamp, decimal Price, SwingPointType Type, string? StructureLabel);

/// <summary>
/// Detects swing highs/lows using a symmetric lookback/lookforward window:
/// bar i is a swing high if its High is the strict maximum among the
/// `lookback` bars on both sides; swing low is the mirror on Low. A swing
/// point near the trailing edge of the series (fewer than `lookback` bars
/// after it) is NOT returned, since it can't yet be confirmed without
/// looking into the future — this is a deliberate no-look-ahead property
/// (spec §0.4), not an oversight: re-run detection once more bars exist.
/// </summary>
public static class SwingPointDetector
{
    public static IReadOnlyList<SwingPoint> Detect(IReadOnlyList<HistoricalBar> bars, int lookback)
    {
        if (lookback < 1) throw new ArgumentOutOfRangeException(nameof(lookback));

        var points = new List<SwingPoint>();

        for (var i = lookback; i < bars.Count - lookback; i++)
        {
            var isSwingHigh = true;
            var isSwingLow = true;

            for (var w = 1; w <= lookback && (isSwingHigh || isSwingLow); w++)
            {
                if (bars[i - w].High >= bars[i].High || bars[i + w].High >= bars[i].High) isSwingHigh = false;
                if (bars[i - w].Low <= bars[i].Low || bars[i + w].Low <= bars[i].Low) isSwingLow = false;
            }

            if (isSwingHigh)
                points.Add(new SwingPoint(i, bars[i].Timestamp, bars[i].High, SwingPointType.High, null));
            if (isSwingLow)
                points.Add(new SwingPoint(i, bars[i].Timestamp, bars[i].Low, SwingPointType.Low, null));
        }

        return LabelStructure(points);
    }

    private static IReadOnlyList<SwingPoint> LabelStructure(List<SwingPoint> points)
    {
        SwingPoint? lastHigh = null;
        SwingPoint? lastLow = null;
        var labeled = new List<SwingPoint>(points.Count);

        foreach (var point in points.OrderBy(p => p.BarIndex))
        {
            string? label = null;

            if (point.Type == SwingPointType.High)
            {
                if (lastHigh is not null)
                    label = point.Price > lastHigh.Price ? "HH" : "LH";
                lastHigh = point;
            }
            else
            {
                if (lastLow is not null)
                    label = point.Price > lastLow.Price ? "HL" : "LL";
                lastLow = point;
            }

            labeled.Add(point with { StructureLabel = label });
        }

        return labeled;
    }
}
