namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "Indicators" (spec §3.4). Every period/multiplier
/// below is a conventional default for its indicator (the same defaults
/// used by Wilder's original formulas and virtually every charting
/// platform), not a tuned strategy parameter — but per spec §0.4 nothing is
/// hardcoded in the calculation code itself: TradingPlatform.Indicators
/// functions all take period/multiplier as explicit parameters, and these
/// are the values Strategy-layer code (Tranche 5+) will pass in, sourced
/// from here rather than literals.
/// </summary>
public sealed class IndicatorOptions
{
    public const string SectionName = "Indicators";

    public int HmaFastPeriod { get; set; } = 9;
    public int HmaSlowPeriod { get; set; } = 21;

    public int SupertrendAtrPeriod { get; set; } = 10;
    public decimal SupertrendMultiplier { get; set; } = 3.0m;

    public int Sma20Period { get; set; } = 20;
    public int Sma50Period { get; set; } = 50;
    public int Sma200Period { get; set; } = 200;

    public int RsiPeriod { get; set; } = 14;

    public int MacdFastPeriod { get; set; } = 12;
    public int MacdSlowPeriod { get; set; } = 26;
    public int MacdSignalPeriod { get; set; } = 9;

    public int AdxPeriod { get; set; } = 14;

    public int AtrPeriod { get; set; } = 14;

    public int HistoricalVolatilityPeriod { get; set; } = 20;
    public int TradingDaysPerYear { get; set; } = 252;

    public int VolumeSmaPeriod { get; set; } = 20;

    /// <summary>Lookback each side for a bar to qualify as a swing high/low (spec §3.4 "price structure").</summary>
    public int SwingPointLookback { get; set; } = 3;
}
