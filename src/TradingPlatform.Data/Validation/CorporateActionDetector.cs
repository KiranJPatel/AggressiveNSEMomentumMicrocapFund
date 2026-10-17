using TradingPlatform.Core.Models;

namespace TradingPlatform.Data.Validation;

/// <summary>
/// Pure heuristic detector for likely stock splits/bonuses (spec §5.3): a
/// large day-over-day move in raw Close that is NOT mirrored in
/// AdjustedClose usually means a split/bonus occurred (Yahoo restates
/// AdjustedClose for the split but the raw Close series shows the jump).
/// This is a heuristic flag for human review, never an automatic "fix" —
/// nothing is altered based on this detection.
/// </summary>
public static class CorporateActionDetector
{
    public static IReadOnlyList<DataQualityIssue> Detect(IReadOnlyList<HistoricalBar> barsAscending, decimal extremeGapPercentThreshold)
    {
        var issues = new List<DataQualityIssue>();

        for (var i = 1; i < barsAscending.Count; i++)
        {
            var prev = barsAscending[i - 1];
            var curr = barsAscending[i];
            if (prev.Close <= 0 || prev.AdjustedClose <= 0) continue;

            var rawMovePercent = Math.Abs((curr.Close - prev.Close) / prev.Close) * 100m;
            if (rawMovePercent < extremeGapPercentThreshold) continue;

            var adjMovePercent = Math.Abs((curr.AdjustedClose - prev.AdjustedClose) / prev.AdjustedClose) * 100m;

            if (adjMovePercent < rawMovePercent / 2m)
            {
                issues.Add(new DataQualityIssue(curr.Symbol, curr.Timestamp, DataQualitySeverity.Warning, "LikelyCorporateAction",
                    $"Raw close moved {rawMovePercent:F1}% day-over-day but adjusted close only moved {adjMovePercent:F1}% — " +
                    "likely a split/bonus/corporate action. Raw OHLC is still used as-is for stop-loss/execution simulation per spec §5.3."));
            }
            else
            {
                issues.Add(new DataQualityIssue(curr.Symbol, curr.Timestamp, DataQualitySeverity.Warning, "ExtremeUnexplainedGap",
                    $"Raw close moved {rawMovePercent:F1}% day-over-day with no corresponding adjusted-close discrepancy — " +
                    "unexplained gap, not attributable to a detected split/bonus. Flagged for manual review."));
            }
        }

        return issues;
    }
}
