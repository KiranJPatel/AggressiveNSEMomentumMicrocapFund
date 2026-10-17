using System.Text.Json;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Backtesting.Selection;

/// <summary>One sweepable strategy parameter: how to write a candidate value into a cloned option set, and how to read the current one.</summary>
public sealed record OptimizerParameter(
    string Name, string Description, IReadOnlyList<decimal> DefaultValues,
    Action<BacktestEngineOptions, decimal> Apply, Func<BacktestEngineOptions, decimal> Read);

/// <summary>
/// The parameters spec §5.5 names for optimization ("RiskPercent, ATR multiplier, RSI/ADX thresholds, volume multiplier, VCP score
/// threshold, breakout threshold, HMA periods, trailing-stop params, regime thresholds") that the engines actually consume.
///
/// NOT included, deliberately: an RSI threshold and a VCP-score threshold. The breakout gate (BreakoutEngine) has no RSI condition and no
/// minimum-VCP-score condition — VCP quality enters only as a 10-point weight inside the stock score — so a sweep dimension with that
/// name would change nothing and every candidate would score identically. Listing a parameter that does nothing would let an optimizer
/// report "robustness" that is just the absence of any effect. If a gate on either is added later, add it here with its engine.
///
/// Invariants between parameters (fast HMA &lt; slow HMA, ordered regime thresholds, ordered trailing triggers, Min &lt;= Max stop
/// multiplier) are checked by <see cref="Validate"/>; a combination that violates one is SKIPPED and counted, never silently "fixed" by
/// moving some other parameter the user did not ask to change.
/// </summary>
public static class OptimizerParameterCatalog
{
    public static IReadOnlyList<OptimizerParameter> All { get; } = new List<OptimizerParameter>
    {
        new("RiskPercentPerTrade", "Risk per trade, % of NAV (RiskOptions)", new[] { 0.5m, 0.75m, 1.0m, 1.25m }, (o, v) => o.Risk.RiskPercentPerTrade = v, o => o.Risk.RiskPercentPerTrade),
        new("StopAtrMultiplierMin", "Lower ATR multiple for the initial stop (RiskOptions)", new[] { 1.5m, 2.0m, 2.5m }, (o, v) => o.Risk.StopAtrMultiplierMin = v, o => o.Risk.StopAtrMultiplierMin),
        new("StopAtrMultiplierMax", "Upper ATR multiple for the initial stop (RiskOptions)", new[] { 2.5m, 3.0m }, (o, v) => o.Risk.StopAtrMultiplierMax = v, o => o.Risk.StopAtrMultiplierMax),
        new("PortfolioHeatMaxPercent", "Maximum open risk across the portfolio, % of NAV (RiskOptions)", new[] { 6m, 8m, 10m }, (o, v) => o.Risk.PortfolioHeatMaxPercent = v, o => o.Risk.PortfolioHeatMaxPercent),
        new("BreakoutMinAdx", "ADX floor of the breakout gate (StrategyOptions)", new[] { 15m, 20m, 25m }, (o, v) => o.Strategy.BreakoutMinAdx = v, o => o.Strategy.BreakoutMinAdx),
        new("BreakoutVolumeMultiplier", "Breakout-day volume vs 20D average (StrategyOptions)", new[] { 1.25m, 1.5m, 2.0m }, (o, v) => o.Strategy.BreakoutVolumeMultiplier = v, o => o.Strategy.BreakoutVolumeMultiplier),
        new("RsBreakoutThreshold", "Relative-strength score floor of the breakout gate (RsOptions)", new[] { 60m, 70m, 80m }, (o, v) => o.Rs.SubstantialOutperformanceScoreThreshold = v, o => o.Rs.SubstantialOutperformanceScoreThreshold),
        new("HmaFastPeriod", "Fast Hull MA period (IndicatorOptions)", new[] { 7m, 9m, 12m }, (o, v) => o.Indicator.HmaFastPeriod = (int)v, o => o.Indicator.HmaFastPeriod),
        new("HmaSlowPeriod", "Slow Hull MA period (IndicatorOptions)", new[] { 18m, 21m, 26m }, (o, v) => o.Indicator.HmaSlowPeriod = (int)v, o => o.Indicator.HmaSlowPeriod),
        new("TrailingAtrMultiplier", "ATR multiple of the trailing stop (ExitOptions)", new[] { 2.5m, 3.0m, 3.5m }, (o, v) => o.Exit.TrailingAtrMultiplier = v, o => o.Exit.TrailingAtrMultiplier),
        new("TrailingActiveTriggerR", "R-multiple at which the active trailing stop starts (StrategyOptions)", new[] { 1.5m, 2m, 2.5m }, (o, v) => o.Strategy.TrailingActiveTriggerR = v, o => o.Strategy.TrailingActiveTriggerR),
        new("MomentumFailureRsThreshold", "RS score below which a position exits as momentum failure (ExitOptions)", new[] { 30m, 40m, 50m }, (o, v) => o.Exit.MomentumFailureRsThreshold = v, o => o.Exit.MomentumFailureRsThreshold),
        new("RegimeAggressiveThreshold", "Regime score at/above which the regime is Aggressive (MarketRegimeOptions)", new[] { 75m, 80m, 85m }, (o, v) => o.MarketRegime.AggressiveThreshold = (int)v, o => o.MarketRegime.AggressiveThreshold),
        new("RegimeNormalThreshold", "Regime score at/above which the regime is Normal (MarketRegimeOptions)", new[] { 60m, 65m, 70m }, (o, v) => o.MarketRegime.NormalThreshold = (int)v, o => o.MarketRegime.NormalThreshold),
    };

    public static OptimizerParameter? Find(string name) => All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deep copy of the option groups the catalog can mutate (Risk, Strategy, Rs, Indicator, Exit, MarketRegime); the groups no catalog
    /// entry touches are shared by reference — nothing writes to them.
    /// </summary>
    public static BacktestEngineOptions Clone(BacktestEngineOptions s) => new()
    {
        Indicator = RoundTrip(s.Indicator), Vcp = s.Vcp, Rs = RoundTrip(s.Rs), StockScoring = s.StockScoring, Strategy = RoundTrip(s.Strategy),
        MarketRegime = RoundTrip(s.MarketRegime), Exit = RoundTrip(s.Exit), Risk = RoundTrip(s.Risk), Portfolio = s.Portfolio, Backtest = s.Backtest,
    };

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    /// <summary>Null when the option set is internally consistent; otherwise the first broken invariant.</summary>
    public static string? Validate(BacktestEngineOptions o)
    {
        if (o.Indicator.HmaFastPeriod >= o.Indicator.HmaSlowPeriod) return $"HmaFastPeriod ({o.Indicator.HmaFastPeriod}) must be below HmaSlowPeriod ({o.Indicator.HmaSlowPeriod})";
        if (o.Risk.StopAtrMultiplierMin > o.Risk.StopAtrMultiplierMax) return $"StopAtrMultiplierMin ({o.Risk.StopAtrMultiplierMin}) exceeds StopAtrMultiplierMax ({o.Risk.StopAtrMultiplierMax})";
        if (o.Risk.RiskPercentPerTrade > o.Risk.MaxRiskPercentPerTrade) return $"RiskPercentPerTrade ({o.Risk.RiskPercentPerTrade}) exceeds MaxRiskPercentPerTrade ({o.Risk.MaxRiskPercentPerTrade})";
        if (o.Risk.RiskPercentPerTrade <= 0m) return "RiskPercentPerTrade must be positive";
        var s = o.Strategy;
        if (!(s.TrailingBreakevenTriggerR < s.TrailingActiveTriggerR && s.TrailingActiveTriggerR < s.TrailingAggressiveTriggerR))
            return $"trailing triggers must satisfy Breakeven ({s.TrailingBreakevenTriggerR}) < Active ({s.TrailingActiveTriggerR}) < Aggressive ({s.TrailingAggressiveTriggerR})";
        var r = o.MarketRegime;
        if (!(r.AggressiveThreshold > r.NormalThreshold && r.NormalThreshold > r.CautiousThreshold && r.CautiousThreshold > r.DefensiveThreshold))
            return $"regime thresholds must descend: Aggressive ({r.AggressiveThreshold}) > Normal ({r.NormalThreshold}) > Cautious ({r.CautiousThreshold}) > Defensive ({r.DefensiveThreshold})";
        return null;
    }
}
