namespace TradingPlatform.Risk.Sizing;

/// <summary>Outcome of re-checking a sized entry against the price it is actually expected to fill at.</summary>
public sealed record GapRiskResult(decimal Stage1Quantity, decimal TotalTargetQuantity, decimal ActualRiskPerShare, bool Resized, bool Cancelled);

/// <summary>
/// Position size is computed at plan time from the PLANNED entry (the VCP pivot) and the stop derived from it: quantity = risk budget /
/// (entry - stop). Entries, however, execute at the next open — and breakouts routinely open ABOVE the pivot. The stop then sits much further
/// below the real fill than it did below the pivot, so the position carries more risk than the budget (on a synthetic 40-stock run the median
/// actual risk was 1.6x the planned risk, up to 2.3x) and its reported R-multiples are inflated.
///
/// This scales the entry DOWN so rupee risk stays at the planned budget: factor = (planned entry - stop) / (expected fill - stop). It never
/// scales up (a fill at or below the pivot already carries no more risk than planned), floors to whole shares, and cancels the entry when the
/// fill price is at or through the stop or the scaled Stage-1 quantity rounds to zero. Pure and deterministic — shared by the backtest and
/// the paper/live cycle so they size identically.
/// </summary>
public static class GapRiskAdjuster
{
    public static GapRiskResult Adjust(decimal stage1Quantity, decimal totalTargetQuantity, decimal plannedEntry, decimal stop, decimal expectedFillPrice)
    {
        var actualRiskPerShare = expectedFillPrice - stop;
        if (actualRiskPerShare <= 0m)
            return new GapRiskResult(0m, 0m, actualRiskPerShare, Resized: false, Cancelled: true);   // already at/through the stop: there is no valid entry

        var plannedRiskPerShare = plannedEntry - stop;
        if (plannedRiskPerShare <= 0m || actualRiskPerShare <= plannedRiskPerShare)
            return new GapRiskResult(stage1Quantity, totalTargetQuantity, actualRiskPerShare, Resized: false, Cancelled: false);

        var factor = plannedRiskPerShare / actualRiskPerShare;
        var q1 = Math.Floor(stage1Quantity * factor);
        var total = Math.Floor(totalTargetQuantity * factor);
        if (q1 <= 0m)
            return new GapRiskResult(0m, 0m, actualRiskPerShare, Resized: false, Cancelled: true);
        return new GapRiskResult(q1, Math.Max(total, q1), actualRiskPerShare, Resized: true, Cancelled: false);
    }
}
