using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// Combines every component from spec §3.2's 100-point stock scoring table
/// into one StockScore. Pure function — every sub-score is either computed
/// from already-supplied bars/indicators (Volume Accumulation, Price
/// Momentum, Trend Structure) or accepted pre-computed from the tranche
/// that owns it (RelativeStrengthScore/SectorStrengthScore from Tranche 5,
/// VcpScore from Tranche 4's VcpPattern).
///
/// Missing-data policy (spec §3.2: "missing data must never silently
/// default to a positive score"): a component whose underlying data is
/// genuinely unavailable (fundamentals, institutional activity, sector
/// classification) contributes 0 to TotalScore, never a fabricated neutral
/// or positive value — and DataQualityScore separately reports what
/// fraction of the total weight was actually backed by real data, so a
/// high TotalScore built on missing components is visibly distinguishable
/// from one built on complete data. "No VCP pattern currently forming" is
/// treated differently: VcpScore = 0 in that case is a genuine assessment
/// (the pattern doesn't exist right now), not missing data, so it counts
/// as "available" for DataQualityScore purposes.
/// </summary>
public static class StockScoringEngine
{
    public static StockScore Calculate(StockScoringInputs inputs, StrategyOptions strategyOptions, StockScoringOptions scoringOptions)
    {
        if (inputs.Bars.Count == 0 || inputs.IndicatorSnapshots.Count == 0)
            throw new ArgumentException("StockScoringEngine requires at least one bar/indicator snapshot.");

        var close = inputs.Bars[^1].Close;
        var currentIndicators = inputs.IndicatorSnapshots[^1];
        var weights = strategyOptions.Weights;

        var relativeStrengthScore = inputs.RelativeStrengthScore;
        var volumeScore = VolumeAccumulationCalculator.Calculate(inputs.IndicatorSnapshots, scoringOptions);
        if (scoringOptions.UseDeliveryPercent && inputs.Delivery is { } delivery)
        {
            // Spec §3.4/§3.2: delivery % refines the volume component. Only when data exists for this symbol AND day; otherwise the
            // relative-volume score stands unchanged — an absent delivery figure is neither rewarded nor punished.
            var w = Math.Clamp(scoringOptions.DeliveryBlendWeightPercent, 0m, 100m) / 100m;
            volumeScore = (1m - w) * volumeScore + w * DeliveryAnalyzer.Score(delivery, scoringOptions);
        }
        var priceMomentumScore = PriceMomentumCalculator.Calculate(inputs.Bars, scoringOptions);
        var vcpScore = inputs.VcpPattern?.VcpScore ?? 0m;
        var trendStructureScore = TrendStructureCalculator.Calculate(close, currentIndicators, strategyOptions);
        var sectorScore = inputs.SectorStrengthScore ?? 0m;
        var sectorAvailable = inputs.SectorStrengthScore is not null;

        var earnings = FundamentalAccelerationCalculator.Calculate(
            inputs.CurrentFundamentals?.EarningsGrowthPercent, inputs.CurrentFundamentals?.EarningsGrowthAvailability ?? DataAvailability.Unknown,
            inputs.PriorFundamentals?.EarningsGrowthPercent, inputs.PriorFundamentals?.EarningsGrowthAvailability ?? DataAvailability.Unknown,
            scoringOptions);

        var revenue = FundamentalAccelerationCalculator.Calculate(
            inputs.CurrentFundamentals?.RevenueGrowthPercent, inputs.CurrentFundamentals?.RevenueGrowthAvailability ?? DataAvailability.Unknown,
            inputs.PriorFundamentals?.RevenueGrowthPercent, inputs.PriorFundamentals?.RevenueGrowthAvailability ?? DataAvailability.Unknown,
            scoringOptions);

        var institutionalAvailable = inputs.InstitutionalActivity?.Availability == DataAvailability.Available
            && inputs.InstitutionalActivity?.ActivityScore is not null;
        var institutionalScore = institutionalAvailable ? inputs.InstitutionalActivity!.ActivityScore!.Value : 0m;

        var totalScore = WeightedSum(
            (relativeStrengthScore, weights.RelativeStrength),
            (volumeScore, weights.VolumeAccumulation),
            (priceMomentumScore, weights.PriceMomentum),
            (earnings.Score, weights.EarningsAcceleration),
            (vcpScore, weights.VcpQuality),
            (revenue.Score, weights.RevenueAcceleration),
            (trendStructureScore, weights.TrendStructure),
            (institutionalScore, weights.InstitutionalActivity),
            (sectorScore, weights.SectorStrength)) / 100m; // weights sum to 100 -> result is already 0-100

        var technicalScore = WeightedAverage(
            (relativeStrengthScore, weights.RelativeStrength),
            (volumeScore, weights.VolumeAccumulation),
            (priceMomentumScore, weights.PriceMomentum),
            (vcpScore, weights.VcpQuality),
            (trendStructureScore, weights.TrendStructure));

        var fundamentalScore = WeightedAverage(
            (earnings.Score, weights.EarningsAcceleration),
            (revenue.Score, weights.RevenueAcceleration),
            (institutionalScore, weights.InstitutionalActivity));

        var availableWeight = weights.RelativeStrength + weights.VolumeAccumulation + weights.PriceMomentum +
            (earnings.IsDataAvailable ? weights.EarningsAcceleration : 0m) +
            weights.VcpQuality +
            (revenue.IsDataAvailable ? weights.RevenueAcceleration : 0m) +
            weights.TrendStructure +
            (institutionalAvailable ? weights.InstitutionalActivity : 0m) +
            (sectorAvailable ? weights.SectorStrength : 0m);

        var dataQualityScore = 100m * availableWeight / TotalWeight(weights);

        return new StockScore
        {
            Symbol = inputs.Symbol,
            AsOfDate = inputs.AsOfDate,
            TotalScore = totalScore,
            TechnicalScore = technicalScore,
            FundamentalScore = fundamentalScore,
            RelativeStrengthScore = relativeStrengthScore,
            VolumeScore = volumeScore,
            PriceMomentumScore = priceMomentumScore,
            EarningsAccelerationScore = earnings.Score,
            VcpScore = vcpScore,
            RevenueAccelerationScore = revenue.Score,
            TrendStructureScore = trendStructureScore,
            InstitutionalActivityScore = institutionalScore,
            SectorScore = sectorScore,
            DataQualityScore = dataQualityScore,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static decimal WeightedSum(params (decimal Score, decimal Weight)[] components) =>
        components.Sum(c => c.Score * c.Weight);

    private static decimal WeightedAverage(params (decimal Score, decimal Weight)[] components)
    {
        var totalWeight = components.Sum(c => c.Weight);
        return totalWeight > 0 ? components.Sum(c => c.Score * c.Weight) / totalWeight : 0m;
    }

    private static decimal TotalWeight(ScoringWeights w) =>
        w.RelativeStrength + w.VolumeAccumulation + w.PriceMomentum + w.EarningsAcceleration +
        w.VcpQuality + w.RevenueAcceleration + w.TrendStructure + w.InstitutionalActivity + w.SectorStrength;
}
