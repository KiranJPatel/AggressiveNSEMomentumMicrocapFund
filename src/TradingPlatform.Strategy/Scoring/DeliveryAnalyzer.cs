using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// Delivery-percent analysis (spec §3.4: "delivery % — if a reliable source is added later"). Point-in-time and conservative:
///  - a snapshot exists only if a record for the EXACT as-of date exists (yesterday's delivery is never carried forward as today's);
///  - the baseline is the average of the previous <c>DeliveryLookbackDays</c> records strictly BEFORE the as-of date and needs at least
///    <c>DeliveryMinSampleDays</c> of them — fewer and the ratio would be noise, so no snapshot (null), never a neutral guess.
/// </summary>
public static class DeliveryAnalyzer
{
    public static DeliverySnapshot? Evaluate(IReadOnlyList<DeliveryRecord> orderedByDate, DateOnly asOf, StockScoringOptions options)
    {
        var today = orderedByDate.FirstOrDefault(r => r.TradeDate == asOf);
        if (today is null) return null;

        var prior = orderedByDate.Where(r => r.TradeDate < asOf).TakeLast(Math.Max(1, options.DeliveryLookbackDays)).ToList();
        if (prior.Count < Math.Max(1, options.DeliveryMinSampleDays)) return null;

        var average = prior.Average(r => r.DeliveryPercent);
        if (average <= 0m) return null;
        return new DeliverySnapshot(today.DeliveryPercent, average, today.DeliveryPercent / average, prior.Count);
    }

    /// <summary>
    /// Snapshot for EVERY record date in one pass (sliding window), identical in result to calling <see cref="Evaluate"/> per date but O(n·window)
    /// instead of O(n²) — a multi-year backtest asks for each symbol's snapshot on every session.
    /// </summary>
    public static Dictionary<DateOnly, DeliverySnapshot> EvaluateSeries(IReadOnlyList<DeliveryRecord> records, StockScoringOptions options)
    {
        var ordered = records.GroupBy(r => r.TradeDate).Select(g => g.First()).OrderBy(r => r.TradeDate).ToList();   // one record per date
        var lookback = Math.Max(1, options.DeliveryLookbackDays);
        var minSample = Math.Max(1, options.DeliveryMinSampleDays);
        var result = new Dictionary<DateOnly, DeliverySnapshot>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = Math.Max(0, i - lookback);
            var count = i - start;
            if (count < minSample) continue;
            decimal sum = 0m;
            for (var j = start; j < i; j++) sum += ordered[j].DeliveryPercent;
            var average = sum / count;
            if (average <= 0m) continue;
            result[ordered[i].TradeDate] = new DeliverySnapshot(ordered[i].DeliveryPercent, average, ordered[i].DeliveryPercent / average, count);
        }
        return result;
    }

    /// <summary>Maps the ratio linearly onto 0-100 between the configured zero-score and full-score ratios.</summary>
    public static decimal Score(DeliverySnapshot snapshot, StockScoringOptions options)
    {
        var lo = options.DeliveryRatioForZeroScore;
        var hi = Math.Max(lo + 0.01m, options.DeliveryRatioForFullScore);
        return Math.Clamp((snapshot.Ratio - lo) / (hi - lo) * 100m, 0m, 100m);
    }
}
