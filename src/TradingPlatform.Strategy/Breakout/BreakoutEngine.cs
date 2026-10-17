using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Breakout;

/// <summary>
/// Implements spec §3.6's breakout formula literally:
///
///   Close > Pivot AND Volume >= 1.5×20D avg volume AND RelativeStrength >= threshold
///   AND ADX >= 20 AND Price > HMA-fast AND HMA-fast > HMA-slow AND Regime allows new positions
///
/// No-look-ahead by construction (spec §3.6): this is a pure function over
/// a single point-in-time Close/IndicatorSnapshot/pivot/RS-score/regime —
/// it has no access to any bar beyond "now" because it's never handed one;
/// there is structurally nothing to look ahead INTO.
///
/// "Volume >= 1.5x 20D avg volume" is exactly IndicatorSnapshot.RelativeVolume
/// (Tranche 3, already Volume/VolumeSma20) >= StrategyOptions.BreakoutVolumeMultiplier
/// — no separate calculation needed. "Regime allows new positions" is a
/// resolved ambiguity (spec §0.4): Emergency-classified regimes halt new
/// entries entirely (existing positions are still managed by the exit/risk
/// engines — that's a separate concern) while every other classification
/// permits new entries, subject to whatever sizing the Risk tranche applies
/// on top of a breakout signal.
/// </summary>
public static class BreakoutEngine
{
    public static BreakoutEvaluation Evaluate(
        decimal close,
        decimal pivotPrice,
        IndicatorSnapshot indicators,
        decimal relativeStrengthScore,
        RegimeClassification regime,
        StrategyOptions strategyOptions,
        RsOptions rsOptions,
        DeliverySnapshot? delivery = null)
    {
        var conditions = new List<BreakoutCondition>
        {
            new("ClosePastPivot", close > pivotPrice, $"Close {close} {(close > pivotPrice ? ">" : "<=")} Pivot {pivotPrice}"),
            EvaluateVolume(indicators.RelativeVolume, strategyOptions.BreakoutVolumeMultiplier),
            new("RelativeStrength",
                relativeStrengthScore >= rsOptions.SubstantialOutperformanceScoreThreshold,
                $"RS score {relativeStrengthScore} {(relativeStrengthScore >= rsOptions.SubstantialOutperformanceScoreThreshold ? ">=" : "<")} threshold {rsOptions.SubstantialOutperformanceScoreThreshold}"),
            EvaluateNullable("Adx", indicators.Adx, v => v >= strategyOptions.BreakoutMinAdx, $">= {strategyOptions.BreakoutMinAdx}"),
            EvaluateNullable("PriceAboveHmaFast", indicators.HmaFast, v => close > v, $"Close {close} > HmaFast"),
            EvaluateHmaFastAboveSlow(indicators.HmaFast, indicators.HmaSlow),
            new("RegimeAllowsNewPositions", regime != RegimeClassification.Emergency, $"Regime classification: {regime}"),
        };

        if (strategyOptions.BreakoutMinDeliveryRatio is { } minRatio)
            conditions.Add(delivery is null
                ? new BreakoutCondition("Delivery", false, "Delivery % unavailable for the signal day (no data imported, or too little history) — cannot confirm, fails closed.")
                : new BreakoutCondition("Delivery", delivery.Ratio >= minRatio,
                    $"Delivery {delivery.TodayPercent:F1}% = {delivery.Ratio:F2}x its {delivery.SampleDays}-day average {(delivery.Ratio >= minRatio ? ">=" : "<")} required {minRatio:F2}x"));

        return new BreakoutEvaluation(conditions.All(c => c.Passed), conditions);
    }

    private static BreakoutCondition EvaluateVolume(decimal? relativeVolume, decimal requiredMultiplier)
    {
        if (relativeVolume is null)
            return new BreakoutCondition("Volume", false, "RelativeVolume unavailable (insufficient history) — cannot confirm, fails closed.");

        return new BreakoutCondition("Volume", relativeVolume.Value >= requiredMultiplier,
            $"RelativeVolume {relativeVolume.Value:F2}x {(relativeVolume.Value >= requiredMultiplier ? ">=" : "<")} required {requiredMultiplier:F2}x");
    }

    private static BreakoutCondition EvaluateNullable(string name, decimal? value, Func<decimal, bool> predicate, string requirementDescription)
    {
        if (value is null)
            return new BreakoutCondition(name, false, $"{name} unavailable (insufficient history) — cannot confirm, fails closed.");

        return new BreakoutCondition(name, predicate(value.Value), $"{name}={value.Value:F2} required {requirementDescription}");
    }

    private static BreakoutCondition EvaluateHmaFastAboveSlow(decimal? hmaFast, decimal? hmaSlow)
    {
        if (hmaFast is null || hmaSlow is null)
            return new BreakoutCondition("HmaFastAboveSlow", false, "HMA fast/slow unavailable (insufficient history) — cannot confirm, fails closed.");

        return new BreakoutCondition("HmaFastAboveSlow", hmaFast.Value > hmaSlow.Value, $"HmaFast {hmaFast.Value:F2} {(hmaFast.Value > hmaSlow.Value ? ">" : "<=")} HmaSlow {hmaSlow.Value:F2}");
    }
}
