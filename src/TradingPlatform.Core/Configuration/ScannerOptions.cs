namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Scanner" (spec §7.1). No magic numbers in code.</summary>
public sealed class ScannerOptions
{
    public const string SectionName = "Scanner";

    public int IntervalMinutes { get; set; } = 15;
    public int TopN { get; set; } = 30;
    public string MarketOpenLocalTime { get; set; } = "09:15";
    public string MarketCloseLocalTime { get; set; } = "15:30";
    public string MarketTimeZoneId { get; set; } = "Asia/Kolkata";

    /// <summary>Spec §7.1 pre-market job runs this many minutes BEFORE the open (data refresh, universe validation, reconciliation, regime).</summary>
    /// <summary>
    /// How many calendar days old a surveillance (ASM/GSM) status may be and still count as current for scan/paper/live. 3 lets
    /// Monday rely on Friday's list; 0 = exact date only. Anything older is Unknown, which fails closed (symbol rejected).
    /// </summary>
    public int SurveillanceMaxStalenessDays { get; set; } = 3;

    public int PreMarketLeadMinutes { get; set; } = 60;
    /// <summary>Spec §7.1 post-market job runs this many minutes AFTER the close (reconciliation, performance, DB snapshot, daily report).</summary>
    public int PostMarketDelayMinutes { get; set; } = 15;
    /// <summary>Daily DB snapshots kept in data/backups; older ones are deleted by the post-market job.</summary>
    public int DatabaseBackupRetentionCount { get; set; } = 14;

    /// <summary>
    /// Spec §2.3 names these as "configured threshold"/"configured minimum"
    /// without giving numbers — resolved here with documented defaults
    /// (₹1cr/day turnover, ₹20 price floor) rather than left as an
    /// implicit gap; both are plain config, easily tuned per spec's own
    /// instruction that nothing here should be a hard-coded literal.
    /// </summary>
    public decimal MinimumAverageDailyTurnover { get; set; } = 1_00_00_000m;
    public decimal MinimumPrice { get; set; } = 20m;

    /// <summary>Enough bars for every indicator/RS window this platform uses, including the longest (180-day RS period) plus warm-up.</summary>
    public int MinimumHistoricalBarsRequired { get; set; } = 200;

    /// <summary>Spec §2.4's "20D avg volume, 20D avg traded value" lookback.</summary>
    public int LiquidityLookbackDays { get; set; } = 20;

    /// <summary>Below this DataQualityScore (spec §3.2), a candidate is treated as HasSevereDataQualityIssue for sizing purposes (Tranche 8's microcap gate) — resolved default, spec names the score component but not a severity cutoff.</summary>
    public decimal SevereDataQualityScoreThreshold { get; set; } = 40m;
}
