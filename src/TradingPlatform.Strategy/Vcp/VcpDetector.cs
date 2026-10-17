using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Structure;

namespace TradingPlatform.Strategy.Vcp;

/// <summary>
/// Detects a Volatility Contraction Pattern (Mark Minervini) as of the most
/// recent bar in the supplied series (spec §3.5). This is a real detector —
/// it walks confirmed swing points and measures actual contraction depths,
/// rather than labeling any sideways price action a "VCP".
///
/// Algorithm (see VcpOptions for every threshold, all configurable):
///  1. Detect swing highs/lows (Indicators.Structure.SwingPointDetector,
///     already no-look-ahead) and reduce to a strict High/Low/High/Low
///     zigzag (ZigzagBuilder).
///  2. Read consecutive (High, Low) pairs off the zigzag as candidate
///     contraction legs. Walk forward accepting legs whose depth is at
///     most MaxContractionRatioOfPrevious times the previous leg's depth;
///     the moment a pullback is NOT meaningfully tighter than the last,
///     the coil is considered broken and the leg list resets, starting
///     fresh from that pullback (this mirrors how published VCP-scanning
///     indicators behave: a failed contraction restarts pattern tracking,
///     it doesn't just get appended).
///  3. The pivot is always the SwingHigh of the most recent accepted leg —
///     "the high of the final, tightest contraction" per the literature —
///     regardless of whether a further (not-yet-broken-into-a-leg) swing
///     high has since formed above it; a swing high confirmed ABOVE the
///     pivot means price likely already broke out, which is out of scope
///     for this detector (that's the Breakout Engine's job, spec §3.6).
///  4. Gate: at least MinContractions legs, and a genuine prior uptrend
///     (MinPriorUptrendPercent rise within PriorUptrendLookbackBars bars
///     before the first accepted leg's high) — without that, this is a
///     base-in-a-downtrend, not a VCP (see VcpOptions class remarks on
///     why Stage-2/trend-template filtering itself is deliberately NOT
///     duplicated here).
///
/// Deliberately pure / no I/O: takes bars, returns a VcpPattern or null.
/// </summary>
public static class VcpDetector
{
    public static VcpPattern? Detect(IReadOnlyList<HistoricalBar> bars, VcpOptions options)
    {
        if (bars.Count == 0) return null;

        var swingPoints = SwingPointDetector.Detect(bars, options.SwingLookback);
        var zigzag = ZigzagBuilder.Build(swingPoints);

        var legs = BuildAcceptedContractionLegs(bars, zigzag, options);
        if (legs.Count < options.MinContractions) return null;

        if (legs.Count > options.MaxContractions)
            legs = legs.Skip(legs.Count - options.MaxContractions).ToList();

        var firstLeg = legs[0];
        if (!HasPriorUptrend(bars, firstLeg.SwingHigh.BarIndex, options)) return null;

        var baseStartIndex = firstLeg.SwingHigh.BarIndex;
        var baseEndIndex = legs[^1].SwingLow.BarIndex;
        var averageVolumeDuringBase = Average(bars, baseStartIndex, baseEndIndex, b => b.Volume);

        var score = ScoreVcp(legs, averageVolumeDuringBase, options, out var volumeDryUpScore);

        return new VcpPattern
        {
            Symbol = bars[^1].Symbol,
            AsOfDate = DateOnly.FromDateTime(bars[^1].Timestamp.DateTime),
            VcpScore = score,
            NumberOfContractions = legs.Count,
            ContractionRatios = legs.Select(l => l.DepthPercent).ToList(),
            PivotPrice = legs[^1].SwingHigh.Price,
            BaseLengthDays = (int)Math.Round((bars[baseEndIndex].Timestamp - bars[baseStartIndex].Timestamp).TotalDays),
            AverageVolumeDuringBase = averageVolumeDuringBase,
            VolumeDryUpScore = volumeDryUpScore,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static List<ContractionLeg> BuildAcceptedContractionLegs(IReadOnlyList<HistoricalBar> bars, IReadOnlyList<SwingPoint> zigzag, VcpOptions options)
    {
        var accepted = new List<ContractionLeg>();

        for (var i = 0; i + 1 < zigzag.Count; i++)
        {
            if (zigzag[i].Type != SwingPointType.High || zigzag[i + 1].Type != SwingPointType.Low) continue;

            var high = zigzag[i];
            var low = zigzag[i + 1];
            if (high.Price <= 0) continue;

            var depth = (high.Price - low.Price) / high.Price * 100m;
            var avgVolume = Average(bars, high.BarIndex, low.BarIndex, b => b.Volume);
            var leg = new ContractionLeg(high, low, depth, avgVolume);

            if (accepted.Count == 0)
            {
                accepted.Add(leg);
                continue;
            }

            var previousDepth = accepted[^1].DepthPercent;
            var tightEnough = previousDepth > 0 && depth <= previousDepth * options.MaxContractionRatioOfPrevious;

            accepted = tightEnough ? Append(accepted, leg) : new List<ContractionLeg> { leg };
        }

        return accepted;
    }

    private static List<ContractionLeg> Append(List<ContractionLeg> list, ContractionLeg item)
    {
        list.Add(item);
        return list;
    }

    private static bool HasPriorUptrend(IReadOnlyList<HistoricalBar> bars, int baseStartBarIndex, VcpOptions options)
    {
        var lookbackStart = Math.Max(0, baseStartBarIndex - options.PriorUptrendLookbackBars);
        if (lookbackStart >= baseStartBarIndex) return false;

        var lowestLow = decimal.MaxValue;
        for (var i = lookbackStart; i <= baseStartBarIndex; i++)
            lowestLow = Math.Min(lowestLow, bars[i].Low);

        if (lowestLow <= 0) return false;

        var baseHigh = bars[baseStartBarIndex].High;
        var risePercent = (baseHigh - lowestLow) / lowestLow * 100m;
        return risePercent >= options.MinPriorUptrendPercent;
    }

    private static decimal ScoreVcp(IReadOnlyList<ContractionLeg> legs, decimal averageVolumeDuringBase, VcpOptions options, out decimal volumeDryUpScore)
    {
        var contractionCountScore = Math.Min((decimal)legs.Count / options.MaxContractions, 1m) * 100m;

        var tighteningScores = new List<decimal>();
        for (var i = 1; i < legs.Count; i++)
        {
            var ratio = legs[i - 1].DepthPercent > 0 ? legs[i].DepthPercent / legs[i - 1].DepthPercent : 1m;
            tighteningScores.Add(Clamp0To100(100m * (1m - ratio)));
        }
        var tighteningConsistencyScore = tighteningScores.Count > 0 ? tighteningScores.Average() : 0m;

        var finalLegVolume = legs[^1].AverageVolume;
        volumeDryUpScore = averageVolumeDuringBase > 0
            ? Clamp0To100(100m * (1m - finalLegVolume / averageVolumeDuringBase))
            : 0m;

        decimal higherLowsScore;
        if (legs.Count < 2)
        {
            higherLowsScore = 100m; // nothing to compare yet — see VcpDetector remarks / VcpOptions.
        }
        else
        {
            var satisfied = 0;
            for (var i = 1; i < legs.Count; i++)
            {
                var minAcceptableLow = legs[i - 1].SwingLow.Price * (1m - options.LowUndercutTolerancePercent / 100m);
                if (legs[i].SwingLow.Price >= minAcceptableLow) satisfied++;
            }
            higherLowsScore = 100m * satisfied / (legs.Count - 1);
        }

        var finalDepth = legs[^1].DepthPercent;
        var finalTightnessScore = options.TightFinalContractionPercent > 0
            ? Clamp0To100(100m * (1m - finalDepth / options.TightFinalContractionPercent))
            : 0m;

        var weightedSum =
            contractionCountScore * options.ContractionCountWeight +
            tighteningConsistencyScore * options.TighteningConsistencyWeight +
            volumeDryUpScore * options.VolumeDryUpWeight +
            higherLowsScore * options.HigherLowsWeight +
            finalTightnessScore * options.FinalTightnessWeight;

        var totalWeight = options.ContractionCountWeight + options.TighteningConsistencyWeight +
                           options.VolumeDryUpWeight + options.HigherLowsWeight + options.FinalTightnessWeight;

        return totalWeight > 0 ? weightedSum / totalWeight : 0m;
    }

    private static decimal Clamp0To100(decimal value) => Math.Clamp(value, 0m, 100m);

    private static decimal Average(IReadOnlyList<HistoricalBar> bars, int fromIndex, int toIndex, Func<HistoricalBar, decimal> selector)
    {
        decimal sum = 0;
        var count = 0;
        for (var i = fromIndex; i <= toIndex; i++)
        {
            sum += selector(bars[i]);
            count++;
        }
        return count > 0 ? sum / count : 0m;
    }
}
