using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.MovingAverages;
using TradingPlatform.Indicators.Volatility;
using TradingPlatform.Strategy.RelativeStrength;

namespace TradingPlatform.Strategy.Regime;

/// <summary>
/// Determines deployable capital via a configurable-signal regime score
/// (spec §3.1). See MarketRegimeInputs for which components are always
/// computable vs. accepted as optional pre-computed inputs, and
/// MarketRegimeOptions for every threshold/weight (all configurable, spec
/// §0.4). RegimeScore is a weighted blend of whichever components are
/// available, renormalized over just those — never a fabricated neutral
/// value standing in for a missing signal.
/// </summary>
public static class MarketRegimeEngine
{
    public static MarketRegimeSnapshot Calculate(MarketRegimeInputs inputs, MarketRegimeOptions options, RsOptions rsOptions, DateOnly asOfDate)
    {
        var niftyTrendScore = ComputeTrendScore(inputs.BroadIndexBars, options);
        var volatilityScore = ComputeVolatilityScore(inputs.BroadIndexBars, options);

        decimal? breadthScore = inputs.PercentUniverseAbove50Dma is not null || inputs.PercentUniverseAbove200Dma is not null
            ? Average(inputs.PercentUniverseAbove50Dma, inputs.PercentUniverseAbove200Dma)
            : null;

        decimal? advanceDeclineScore = inputs.AdvanceDeclineRatio is { } ratio ? ComputeAdvanceDeclineScore(ratio) : null;

        decimal? smallMidRsScore = null;
        if (inputs.MidcapIndexBars is { Count: > 0 } midcapBars)
        {
            var (midcapCloses, broadCloses) = PriceSeriesAligner.AlignByDate(midcapBars, inputs.BroadIndexBars);
            if (midcapCloses.Count > 0)
                smallMidRsScore = RelativeStrengthCalculator.Calculate(midcapCloses, broadCloses, rsOptions).CompositeScore;
        }

        var components = new List<(decimal? Score, decimal Weight)>
        {
            (niftyTrendScore, options.NiftyTrendWeight),
            (breadthScore, options.BreadthWeight),
            (advanceDeclineScore, options.AdvanceDeclineWeight),
            (smallMidRsScore, options.SmallMidRelativeStrengthWeight),
            (volatilityScore, options.VolatilityWeight),
            (inputs.BreakoutSuccessRatePercent, options.BreakoutSuccessRateWeight),
        };

        // Fail closed (spec §0.4): with no index trend (history shorter than the SMA200) or no usable component at all, the market
        // cannot be judged — volatility alone would read a calm, untrended market as Aggressive, and an empty set used to read as a
        // neutral 50 (Cautious, 50% deployable). Both now mean RegimeScore 0 and nothing deployable.
        var insufficientData = (niftyTrendScore is null && options.FailClosedWithoutTrend) || components.All(c => c.Score is null);
        int regimeScore;
        RegimeClassification classification;
        decimal deployablePercent;
        if (insufficientData)
        {
            regimeScore = 0;
            classification = RegimeClassification.Emergency;
            deployablePercent = 0m;
        }
        else
        {
            var regimeScoreDecimal = WeightedAverage(components);
            regimeScore = (int)Math.Round(regimeScoreDecimal, MidpointRounding.AwayFromZero);
            (classification, deployablePercent) = RegimeClassifier.Classify(regimeScore, options);
        }

        // Weekly bias (spec §7.3): always computed and recorded; it only changes exposure if the operator configured a cap, and then only downward.
        var weeklyBias = WeeklyMarketBiasCalculator.Calculate(inputs.BroadIndexBars, options);
        if (weeklyBias == WeeklyBias.Bearish && options.WeeklyBearishDeployableCapPercent is { } cap)
            deployablePercent = Math.Min(deployablePercent, Math.Max(0m, cap));

        return new MarketRegimeSnapshot
        {
            AsOfDate = asOfDate,
            RegimeScore = regimeScore,
            Classification = classification,
            DeployableCapitalPercent = deployablePercent,
            NiftyTrendScore = niftyTrendScore,
            BreadthScore = breadthScore,
            AdvanceDeclineRatio = inputs.AdvanceDeclineRatio,
            PercentUniverseAbove50Dma = inputs.PercentUniverseAbove50Dma,
            PercentUniverseAbove200Dma = inputs.PercentUniverseAbove200Dma,
            SmallMidRelativeStrength = smallMidRsScore,
            VolatilityScore = volatilityScore,
            BreakoutSuccessRate = inputs.BreakoutSuccessRatePercent,
            WeeklyBias = weeklyBias,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Four bullish/bearish sub-signals, each 0 or 100, averaged: Close&gt;SMA50, Close&gt;SMA200, SMA50&gt;SMA200 (golden cross), and SMA200 rising over TrendSlopeLookbackDays.</summary>
    private static decimal? ComputeTrendScore(IReadOnlyList<HistoricalBar> bars, MarketRegimeOptions options)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var sma50 = SimpleMovingAverage.Calculate(closes, options.TrendSma50Period);
        var sma200 = SimpleMovingAverage.Calculate(closes, options.TrendSma200Period);

        var last = closes.Count - 1;
        if (last < 0 || sma50[last] is null || sma200[last] is null) return null;

        var lookbackIndex = last - options.TrendSlopeLookbackDays;
        var sma200Rising = lookbackIndex >= 0 && sma200[lookbackIndex] is not null && sma200[last]!.Value > sma200[lookbackIndex]!.Value;

        var signals = new[]
        {
            closes[last] > sma50[last]!.Value,
            closes[last] > sma200[last]!.Value,
            sma50[last]!.Value > sma200[last]!.Value,
            sma200Rising
        };

        return 100m * signals.Count(s => s) / signals.Length;
    }

    /// <summary>Annualized historical volatility mapped inversely onto 0-100 (calm market = high score).</summary>
    private static decimal? ComputeVolatilityScore(IReadOnlyList<HistoricalBar> bars, MarketRegimeOptions options)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var hv = TradingPlatform.Indicators.Volatility.HistoricalVolatility.Calculate(closes, options.VolatilityPeriod, options.VolatilityTradingDaysPerYear);

        var last = hv.Count - 1;
        if (last < 0 || hv[last] is null) return null;

        var value = hv[last]!.Value;
        var low = options.VolatilityLowThresholdPercent;
        var high = options.VolatilityHighThresholdPercent;
        if (high <= low) return null;

        if (value <= low) return 100m;
        if (value >= high) return 0m;

        return 100m * (1m - (value - low) / (high - low));
    }

    /// <summary>Advance/decline ratio (1.0 = neutral) squashed to 0-100 via tanh of its log — symmetric for equally-strong advance- and decline-dominated readings.</summary>
    private static decimal ComputeAdvanceDeclineScore(decimal ratio)
    {
        if (ratio <= 0) return 0m;
        var logRatio = (decimal)Math.Log((double)ratio);
        return ScoreNormalization.SquashToScore(logRatio, 1.0m); // scale of 1.0 natural-log-unit is a wide, sensible default for an AD ratio
    }

    private static decimal? Average(decimal? a, decimal? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return (a.Value + b.Value) / 2m;
    }

    private static decimal WeightedAverage(IReadOnlyList<(decimal? Score, decimal Weight)> components)
    {
        decimal weightedSum = 0, totalWeight = 0;
        foreach (var (score, weight) in components)
        {
            if (score is null) continue;
            weightedSum += score.Value * weight;
            totalWeight += weight;
        }
        return totalWeight > 0 ? weightedSum / totalWeight : 0m; // unreachable via Calculate (all-null is handled above); never a neutral 50
    }
}
