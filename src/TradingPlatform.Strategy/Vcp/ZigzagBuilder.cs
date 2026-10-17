using TradingPlatform.Indicators.Structure;

namespace TradingPlatform.Strategy.Vcp;

/// <summary>
/// Reduces raw swing-point output (which can contain consecutive points of
/// the same type — e.g. two swing highs with no swing low confirmed
/// between them, if the second high's lookback/lookforward window doesn't
/// overlap the first) into a strictly-alternating High/Low/High/Low zigzag,
/// the standard cleanup step before reading off contraction legs. When two
/// consecutive raw points share a type, only the more extreme one survives
/// (higher of two Highs, lower of two Lows) — the less extreme one was
/// never really "the" swing point, just noise inside a bigger move.
/// </summary>
public static class ZigzagBuilder
{
    public static IReadOnlyList<SwingPoint> Build(IReadOnlyList<SwingPoint> rawSwingPoints)
    {
        var ordered = rawSwingPoints.OrderBy(p => p.BarIndex).ToList();
        var zigzag = new List<SwingPoint>();

        foreach (var point in ordered)
        {
            if (zigzag.Count == 0)
            {
                zigzag.Add(point);
                continue;
            }

            var last = zigzag[^1];
            if (last.Type != point.Type)
            {
                zigzag.Add(point);
                continue;
            }

            // Same type as the last kept point: keep whichever is more extreme.
            var keepNew = point.Type == SwingPointType.High
                ? point.Price > last.Price
                : point.Price < last.Price;

            if (keepNew) zigzag[^1] = point;
        }

        return zigzag;
    }
}
