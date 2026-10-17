using TradingPlatform.Core.Models;

namespace TradingPlatform.Data.Validation;

/// <summary>
/// Pure validation logic over a sequence of bars for one symbol/timeframe,
/// already sorted ascending by Timestamp (spec §5.3). No I/O — fully unit
/// testable. Checks: invalid OHLC relationships, non-positive prices, zero
/// volume, and duplicate timestamps within the batch (the SQLite unique
/// index also catches duplicates at persist time, but flagging here surfaces
/// it earlier, before a write even happens).
/// </summary>
public static class HistoricalBarValidator
{
    public static IReadOnlyList<DataQualityIssue> Validate(IReadOnlyList<HistoricalBar> bars)
    {
        var issues = new List<DataQualityIssue>();
        DateTimeOffset? previousTimestamp = null;

        foreach (var bar in bars)
        {
            if (bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Error, "NonPositivePrice",
                    $"One or more OHLC values is zero or negative (O={bar.Open} H={bar.High} L={bar.Low} C={bar.Close})."));

            if (bar.High < bar.Low)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Error, "InvalidHighLow",
                    $"High ({bar.High}) is less than Low ({bar.Low})."));

            if (bar.High < bar.Open || bar.High < bar.Close)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Error, "HighBelowOpenOrClose",
                    $"High ({bar.High}) is less than Open ({bar.Open}) or Close ({bar.Close})."));

            if (bar.Low > bar.Open || bar.Low > bar.Close)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Error, "LowAboveOpenOrClose",
                    $"Low ({bar.Low}) is greater than Open ({bar.Open}) or Close ({bar.Close})."));

            if (bar.Volume == 0)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Info, "ZeroVolume",
                    "Volume is zero — can be legitimate for a halted/illiquid session, flagged for awareness only."));

            if (bar.Volume < 0)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Error, "NegativeVolume",
                    $"Volume is negative ({bar.Volume})."));

            if (previousTimestamp == bar.Timestamp)
                issues.Add(new DataQualityIssue(bar.Symbol, bar.Timestamp, DataQualitySeverity.Warning, "DuplicateTimestampInBatch",
                    "Two bars in the same fetched batch share this timestamp."));

            previousTimestamp = bar.Timestamp;
        }

        return issues;
    }
}
