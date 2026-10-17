using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// "Volume Accumulation" component (spec §3.2, 15 pts): sustained
/// above-average volume over a recent window — not a single day's spike
/// (that's what Breakout's 1.5x-relative-volume gate already checks).
/// Averages IndicatorSnapshot.RelativeVolume (Tranche 3) over the window;
/// RelativeVolume == 1.0 is exactly average, so the mean sits at 50
/// (neutral) by construction, not as a separate calibration.
/// </summary>
public static class VolumeAccumulationCalculator
{
    public static decimal Calculate(IReadOnlyList<IndicatorSnapshot> recentSnapshots, StockScoringOptions options)
    {
        var window = recentSnapshots
            .TakeLast(options.VolumeAccumulationWindowBars)
            .Select(s => s.RelativeVolume)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (window.Count == 0) return 50m; // no data — neutral, not fabricated-positive

        var avgRelativeVolume = window.Average();
        return ScoreNormalization.SquashToScore(avgRelativeVolume - 1m, options.VolumeAccumulationNormalizationScale);
    }
}
