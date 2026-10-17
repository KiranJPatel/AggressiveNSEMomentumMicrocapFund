namespace TradingPlatform.Indicators.MovingAverages;

/// <summary>
/// Hull Moving Average (Alan Hull, 2005): HMA(n) = WMA(2*WMA(n/2) - WMA(n), sqrt(n)).
/// Confirmed formula (alanhull.com, StockCharts ChartSchool, TradingView).
/// n/2 and sqrt(n) are both rounded to the nearest integer using round-half-
/// away-from-zero (e.g. n=9 -> half=5, not 4) — the sources describe
/// rounding "to the nearest whole number" without pinning down the .5 case,
/// so this is a deliberate, explicit choice, not an accident. It matters:
/// n=9 with banker's rounding (half=4, Python's default round()) produces a
/// materially different series than half=5 — this was caught by a
/// cross-language reference-value test disagreeing by ~0.6 on first run
/// (see docs/gap-log.md) before both implementations were reconciled onto
/// this same convention.
/// </summary>
public static class HullMovingAverage
{
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period), "HMA period must be >= 2.");

        var halfPeriod = (int)Math.Round(period / 2.0, MidpointRounding.AwayFromZero);
        var sqrtPeriod = (int)Math.Round(Math.Sqrt(period), MidpointRounding.AwayFromZero);
        if (halfPeriod < 1) halfPeriod = 1;
        if (sqrtPeriod < 1) sqrtPeriod = 1;

        var wmaHalf = WeightedMovingAverage.Calculate(values, halfPeriod);
        var wmaFull = WeightedMovingAverage.Calculate(values, period);

        var raw = new decimal?[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            raw[i] = wmaHalf[i].HasValue && wmaFull[i].HasValue
                ? 2m * wmaHalf[i]!.Value - wmaFull[i]!.Value
                : null;
        }

        // WeightedMovingAverage.Calculate requires non-null decimal values;
        // feed it zeros for the not-yet-warmed-up prefix. A value is only
        // genuinely valid once the smoothing WMA's own window (sqrtPeriod
        // wide) is built entirely from real (non-null) raw values — not
        // just once raw[i] itself first becomes non-null — otherwise the
        // first few "valid" outputs would silently include zero-filled
        // padding in their weighted average.
        var rawFilled = raw.Select(v => v ?? 0m).ToList();
        var smoothed = WeightedMovingAverage.Calculate(rawFilled, sqrtPeriod);

        var firstValidRawIndex = period - 1;
        var firstValidSmoothedIndex = firstValidRawIndex + sqrtPeriod - 1;

        var result = new decimal?[values.Count];
        for (var i = 0; i < values.Count; i++)
            result[i] = i >= firstValidSmoothedIndex ? smoothed[i] : null;

        return result;
    }
}
