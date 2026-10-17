using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Strategy.Entry;

/// <summary>
/// Splits a total target quantity into the three stage quantities (spec
/// §3.7: 40%/30%/30%). Stage 1 and Stage 2 are floored to whole shares;
/// Stage 3 absorbs whatever's left (total minus the other two), which
/// guarantees the three quantities always sum EXACTLY to the total —
/// simply flooring all three independently could under-allocate by 1-2
/// shares due to rounding, silently leaving the position short of its
/// intended size.
/// </summary>
public static class StagedEntryCalculator
{
    public static (decimal Stage1Qty, decimal Stage2Qty, decimal Stage3Qty) Split(decimal totalQuantity, StrategyOptions options)
    {
        if (totalQuantity < 0) throw new ArgumentOutOfRangeException(nameof(totalQuantity));

        var stage1 = Math.Floor(totalQuantity * options.Stage1PercentOfPosition / 100m);
        var stage2 = Math.Floor(totalQuantity * options.Stage2PercentOfPosition / 100m);
        var stage3 = totalQuantity - stage1 - stage2;

        return (stage1, stage2, stage3);
    }
}
