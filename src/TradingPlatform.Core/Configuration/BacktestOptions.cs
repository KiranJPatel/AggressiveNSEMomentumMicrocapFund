namespace TradingPlatform.Core.Configuration;

using TradingPlatform.Core.Enums;

/// <summary>Binds to config section "Backtest" (spec §5.1-§5.5). No magic numbers in code.</summary>
public sealed class BacktestOptions
{
    public const string SectionName = "Backtest";

    public SignalToOrderLag SignalToOrderLag { get; set; } = SignalToOrderLag.NextOpen;

    // --- Intraday entry refinement (spec §3.8 / §7.3). Only consulted when SignalToOrderLag is Refinement60m or Refinement15m. ---

    /// <summary>The refining hourly bar must CLOSE this much (percent) above the breakout level to confirm the entry.</summary>
    public decimal RefinementConfirmBufferPercent { get; set; } = 0m;

    /// <summary>The confirming hourly bar's volume must be at least this multiple of the symbol's average hourly volume over the previous sessions (skipped when no baseline exists).</summary>
    public decimal RefinementMinTriggerRelativeVolume { get; set; } = 1.0m;

    /// <summary>How many previous sessions' hourly bars form the volume baseline.</summary>
    public int RefinementVolumeBaselineSessions { get; set; } = 5;

    /// <summary>
    /// What to do when a session has no cached intraday bars (Yahoo only serves ~59 days of 5m/15m and ~729 of 60m, so long backtests are
    /// mostly uncovered). False (default): fall back to the plain daily-open fill and COUNT it, so the report shows how much of the run was really
    /// refined. True: skip the entry — the run then only trades sessions that have intraday data.
    /// </summary>
    public bool RefinementSkipWhenNoIntradayData { get; set; } = false;

    public SlippageModelType SlippageModel { get; set; } = SlippageModelType.LiquidityBased;
    public decimal FixedSlippageBps { get; set; } = 10m;

    /// <summary>
    /// Spec §5.4 names three slippage models by name only (FixedBps,
    /// Percentage, LiquidityBased) without defining what distinguishes
    /// "Percentage" from "FixedBps" — both would otherwise be the same
    /// mathematical shape (price scaled by a constant). Resolved here
    /// (spec §0.4) as a volatility-scaled model: a percentage of the
    /// EXECUTION BAR'S OWN (High-Low) range, converted to bps of Close —
    /// captures day-specific volatility a flat spread can't, and gives
    /// "Percentage" a genuinely distinct meaning from "FixedBps" rather
    /// than being a redundant reparameterization of it.
    /// </summary>
    public decimal PercentageSlippageOfRangePercent { get; set; } = 10m;

    /// <summary>LiquidityBased model's floor — the bps applied even at negligible participation (spec §5.4: this model is the backtest default).</summary>
    public decimal LiquidityBasedMinBps { get; set; } = 5m;

    /// <summary>Additional bps at 100% ADTV participation (linearly scaled below that) — the "dynamically widens... as liquidity deteriorates" half of spec §5.4's LiquidityBased description.</summary>
    public decimal LiquidityBasedMaxParticipationBpsScaling { get; set; } = 50m;

    /// <summary>Multiplies the whole LiquidityBased bps figure for microcap candidates — the "widens for microcaps" half of spec §5.4's LiquidityBased description.</summary>
    public decimal LiquidityBasedMicrocapMultiplier { get; set; } = 2m;

    /// <summary>
    /// Rolling lookback used to compute Average Daily Traded Value from raw
    /// historical bars during simulation (spec §2.4's ADTV concept, but
    /// computed here rather than read from Instrument.AverageDailyTurnover
    /// — that field is a live/current snapshot; a backtest at an arbitrary
    /// historical date needs the rolling figure AS OF that date instead).
    /// </summary>
    public int AdtvLookbackDays { get; set; } = 20;

    public CommissionOptions Commission { get; set; } = new();

    public int WalkForwardTrainYears { get; set; } = 3;
    public int WalkForwardValidateMonths { get; set; } = 6;
    public int WalkForwardTestMonths { get; set; } = 6;

    public int MonteCarloIterations { get; set; } = 1000;
    public decimal MaxAcceptableDrawdownPercent { get; set; } = 40m;

    /// <summary>
    /// Performance opt-in (Tranche 18, spec §7.5 "optimize for production
    /// scale"): BacktestEngine's daily loop re-slices each symbol's bars
    /// "up to today" fresh every day for VCP/RS/trailing-stop/momentum
    /// evaluation (documented in BacktestEngine's class remarks as a known
    /// O(n^2)-per-symbol characteristic — indicators are the sole
    /// exception, computed once up front). When this is null (the
    /// default), that slice is the full history-to-date, unchanged from
    /// every prior tranche's exact behavior — zero risk to the 600+
    /// existing tests, none of which were re-run against a bounded window
    /// (no dotnet SDK in this sandbox to verify a behavior-preserving
    /// claim empirically — see docs/gap-log.md). When set to a positive
    /// bar count, every such slice is capped to at most that many of the
    /// most recent bars ending "today" instead of growing unboundedly —
    /// turning the per-symbol cost from O(n^2) into O(n * window), which
    /// matters once a run spans several years. The default recommended
    /// value (set explicitly in appsettings.json, not silently applied)
    /// is 500 trading days: comfortably above every stated lookback need
    /// in this codebase (Sma200Period=200, RS Periods max=180,
    /// Vcp.PriorUptrendLookbackBars=60, StockScoring.PriceMomentumPeriodBars=60)
    /// with a wide safety margin for VCP's own unbounded internal base
    /// search (no MaxBaseLengthDays exists in VcpOptions — see its class
    /// remarks), while still being far smaller than an unbounded slice on
    /// a multi-year run. Operators should re-run BacktestingTests after
    /// enabling this and before depending on it for capital-affecting
    /// decisions, since the claim above is a careful hand-trace, not a
    /// compiler- and test-suite-verified guarantee.
    /// </summary>
    public int? BoundedLookbackWindowBars { get; set; }
}

/// <summary>
/// Transaction cost rates — must be verified against current brokerage/
/// exchange/regulatory rates before live deployment (spec §5.4).
/// </summary>
public sealed class CommissionOptions
{
    public decimal BrokeragePercent { get; set; } = 0.03m;
    public decimal BrokerageMaxPerOrder { get; set; } = 20m;
    public decimal SttPercent { get; set; } = 0.1m;
    public decimal ExchangeChargesPercent { get; set; } = 0.00345m;
    public decimal GstPercent { get; set; } = 18m;
    public decimal SebiChargesPercent { get; set; } = 0.0001m;
    public decimal StampDutyPercent { get; set; } = 0.015m;
    public bool RatesVerifiedCurrent { get; set; } = false;
}
