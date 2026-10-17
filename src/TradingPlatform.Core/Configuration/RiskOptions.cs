namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Risk" (spec §4.1-§4.5). No magic numbers in code.</summary>
public sealed class RiskOptions
{
    public const string SectionName = "Risk";

    /// <summary>
    /// When true (default), an entry whose expected fill price is ABOVE the planned entry (the VCP pivot) is scaled down so its rupee risk stays at
    /// the planned budget (GapRiskAdjuster) — and cancelled if the fill is at/through the stop. Never scales up. The same switch also makes the backtest record each
    /// position's risk (and so its R-multiples and R-based trailing/pyramid triggers) from the ACTUAL fill price instead of the planned pivot entry.
    /// Set false to reproduce the older behaviour exactly: size from the pivot, record planned risk, accept whatever risk the real fill carries.
    /// </summary>
    public bool ResizeEntryForGapRisk { get; set; } = true;

    public decimal RiskPercentPerTrade { get; set; } = 1.0m;
    public decimal MaxRiskPercentPerTrade { get; set; } = 1.25m;

    public decimal StopAtrMultiplierMin { get; set; } = 2.0m;
    public decimal StopAtrMultiplierMax { get; set; } = 2.5m;

    /// <summary>ATR period used specifically for initial-stop placement (spec §4.2) — deliberately its own setting, not necessarily the same as IndicatorOptions.AtrPeriod used for general indicator display.</summary>
    public int AtrPeriodForStop { get; set; } = 14;

    /// <summary>Swing-point lookback used specifically for the "technical swing-low invalidation" half of the initial-stop formula (spec §4.2).</summary>
    public int StopSwingLookback { get; set; } = 3;

    public decimal PortfolioHeatMaxPercent { get; set; } = 8m;
    public int CorrelationWindowDays { get; set; } = 60;

    public decimal MaxParticipationPercentOfAdtv { get; set; } = 2m;

    public decimal MicrocapMinTechnicalScore { get; set; } = 80m;
    public decimal MicrocapMinFundamentalScore { get; set; } = 60m;

    /// <summary>
    /// StockScore.TotalScore at/above which a position may use
    /// PortfolioOptions.ExceptionalPositionPercent instead of
    /// MaxSinglePositionPercent as its sizing cap (spec §2.1 defines both
    /// percentages but not when "exceptional" applies — resolved here,
    /// documented per spec §0.4, rather than left as dead config).
    /// </summary>
    public decimal ExceptionalPositionScoreThreshold { get; set; } = 85m;

    /// <summary>Pearson correlation (60-day rolling, spec §4.2) at/above which two positions are treated as part of the same "cluster" for exposure-capping purposes.</summary>
    public decimal CorrelationClusterThreshold { get; set; } = 0.7m;

    /// <summary>Aggregate exposure cap for a correlated cluster (spec §4.2: "ten highly-correlated names must not count as ten independent bets") — same mechanism as MaxSectorExposurePercent, applied to correlation clusters instead of sectors.</summary>
    public decimal MaxCorrelatedClusterExposurePercent { get; set; } = 25m;

    public decimal DailyLossLimitPercent { get; set; } = 2m;
    public decimal WeeklyLossLimitPercent { get; set; } = 4m;
    public decimal MonthlyLossLimitPercent { get; set; } = 8m;

    /// <summary>
    /// Spec §4.5 says breaching the daily limit "stops new trades that day" —
    /// an unambiguous full block, not a magnitude — so DailyLossLimitPercent
    /// alone is enough to implement it (PortfolioRiskGate: block, don't scale).
    /// For the weekly ("reduce risk") and monthly ("defensive mode") limits,
    /// the spec names the trigger threshold but not how much to reduce by —
    /// unlike the fully-numeric DrawdownTiers table. Resolved here (spec
    /// §0.4: resolved ambiguities must be documented, not silently guessed)
    /// as an additional deployable-capital reduction, combined with every
    /// other constraint via MIN in PortfolioRiskGate — never a full block,
    /// since only the daily limit's spec wording says "stop".
    /// </summary>
    public decimal WeeklyLossLimitRiskReductionPercent { get; set; } = 50m;

    /// <summary>Monthly's "defensive mode" is resolved as a deeper cut than weekly's "reduce risk" — see WeeklyLossLimitRiskReductionPercent's doc for the same reasoning.</summary>
    public decimal MonthlyLossLimitRiskReductionPercent { get; set; } = 75m;

    /// <summary>
    /// Ordered ascending by drawdown threshold; see spec §4.5 table. Deliberately
    /// starts EMPTY, not pre-populated: Microsoft.Extensions.Configuration's
    /// ConfigurationBinder appends JSON array items onto an existing non-empty
    /// List&lt;T&gt; property rather than replacing it (a well-known binder
    /// behavior, caught by ConfigurationBindingTests.AppSettingsJson_BindsToRiskOptions_WithEightDrawdownTiers) —
    /// pre-populating this would silently double the tiers whenever config
    /// binds. The canonical values always come from config/appsettings.json;
    /// see <see cref="DefaultDrawdownTiers"/> for code-only (no-config) use.
    /// </summary>
    public List<DrawdownTier> DrawdownTiers { get; set; } = new();

    /// <summary>Canonical spec §4.5 tiers, for code paths that construct RiskOptions without binding config (e.g. isolated unit tests).</summary>
    public static IReadOnlyList<DrawdownTier> DefaultDrawdownTiers { get; } = new List<DrawdownTier>
    {
        new DrawdownTier { ThresholdPercent = 0,  RiskReductionPercent = 0,   Label = "FullAggression" },
        new DrawdownTier { ThresholdPercent = 10, RiskReductionPercent = 0,   Label = "NormalReduced" },
        new DrawdownTier { ThresholdPercent = 15, RiskReductionPercent = 20,  Label = "ReduceRisk20" },
        new DrawdownTier { ThresholdPercent = 20, RiskReductionPercent = 40,  Label = "ReduceRisk40" },
        new DrawdownTier { ThresholdPercent = 25, RiskReductionPercent = 50,  Label = "HalfExposure" },
        new DrawdownTier { ThresholdPercent = 30, RiskReductionPercent = 100, Label = "CapitalPreservation" },
        new DrawdownTier { ThresholdPercent = 35, RiskReductionPercent = 100, Label = "Emergency" },
        new DrawdownTier { ThresholdPercent = 40, RiskReductionPercent = 100, Label = "TradingShutdown", IsShutdown = true },
    };
}

public sealed class DrawdownTier
{
    public decimal ThresholdPercent { get; set; }
    public decimal RiskReductionPercent { get; set; }
    public required string Label { get; set; }
    public bool IsShutdown { get; set; }
}
