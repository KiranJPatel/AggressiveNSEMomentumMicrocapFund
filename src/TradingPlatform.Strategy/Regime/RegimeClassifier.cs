using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;

namespace TradingPlatform.Strategy.Regime;

/// <summary>Maps a 0-100 RegimeScore to a classification band and deployable-capital % (spec §3.1). Pure, tested independently of the scoring inputs.</summary>
public static class RegimeClassifier
{
    public static (RegimeClassification Classification, decimal DeployableCapitalPercent) Classify(int regimeScore, MarketRegimeOptions options)
    {
        if (regimeScore >= options.AggressiveThreshold)
            return (RegimeClassification.Aggressive, options.AggressiveExposurePercent);
        if (regimeScore >= options.NormalThreshold)
            return (RegimeClassification.Normal, options.NormalExposurePercent);
        if (regimeScore >= options.CautiousThreshold)
            return (RegimeClassification.Cautious, options.CautiousExposurePercent);
        if (regimeScore >= options.DefensiveThreshold)
            return (RegimeClassification.Defensive, options.DefensiveExposurePercent);

        return (RegimeClassification.Emergency, options.EmergencyExposurePercent);
    }
}
