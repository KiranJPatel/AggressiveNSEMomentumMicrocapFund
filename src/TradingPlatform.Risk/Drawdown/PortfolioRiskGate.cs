using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Risk.Drawdown;

/// <summary>
/// Combines DrawdownEngine's circuit-breaker tier with
/// PeriodicLossLimitEngine's weekly/monthly reductions into one
/// deployable-capital percentage and one new-entry gate, using the same
/// "most conservative constraint wins" (MIN) principle
/// PositionSizingCalculator already applies across its 9 sizing
/// constraints (Tranche 8) — a bad week during a mild drawdown should be
/// exactly as defensive as the worse of the two, never averaged away.
///
/// The daily loss limit is deliberately NOT one of the percentage
/// candidates: spec §4.5 says it "stops new trades that day" — an
/// unambiguous full block for the rest of that trading day, not a
/// magnitude to blend in — so it surfaces only through BlocksNewEntries /
/// BlockingReasons, the same way DrawdownEngine's shutdown tier does.
///
/// This result covers only the drawdown/loss-limit axis. It is NOT by
/// itself PositionSizingInputs.DeployableCapitalPercent — the orchestrator
/// that eventually wires Backtesting (Tranche 11) or Live (Tranche 15)
/// must take MIN(this.DeployableCapitalPercent, RegimeClassifier's own
/// deployable-capital percent from MarketRegimeSnapshot, Tranche 5) before
/// handing that number to PositionSizingCalculator — this engine
/// deliberately has no visibility into market regime, so it cannot and
/// must not claim to produce that final combined number alone.
/// </summary>
public static class PortfolioRiskGate
{
    public static PortfolioRiskGateResult Evaluate(DrawdownState drawdownState, LossLimitState lossLimitState, RiskOptions options)
    {
        var drawdownBasedPercent = Math.Clamp(100m - drawdownState.CurrentTier.RiskReductionPercent, 0m, 100m);
        var weeklyBasedPercent = lossLimitState.WeeklyLimitBreached
            ? Math.Clamp(100m - options.WeeklyLossLimitRiskReductionPercent, 0m, 100m)
            : 100m;
        var monthlyBasedPercent = lossLimitState.MonthlyLimitBreached
            ? Math.Clamp(100m - options.MonthlyLossLimitRiskReductionPercent, 0m, 100m)
            : 100m;

        var candidates = new (string Name, decimal Percent)[]
        {
            (nameof(PortfolioRiskGateResult.DrawdownBasedDeployablePercent), drawdownBasedPercent),
            (nameof(PortfolioRiskGateResult.WeeklyBasedDeployablePercent), weeklyBasedPercent),
            (nameof(PortfolioRiskGateResult.MonthlyBasedDeployablePercent), monthlyBasedPercent),
        };
        var deployableCapitalPercent = candidates.Min(c => c.Percent);
        var bindingConstraints = candidates.Where(c => c.Percent == deployableCapitalPercent).Select(c => c.Name).ToList();

        var blockingReasons = new List<string>();
        if (drawdownState.IsTradingShutdown) blockingReasons.Add("TradingShutdown");
        if (lossLimitState.DailyLimitBreached) blockingReasons.Add("DailyLossLimitBreached");

        return new PortfolioRiskGateResult
        {
            DrawdownBasedDeployablePercent = drawdownBasedPercent,
            WeeklyBasedDeployablePercent = weeklyBasedPercent,
            MonthlyBasedDeployablePercent = monthlyBasedPercent,
            DeployableCapitalPercent = deployableCapitalPercent,
            BindingConstraints = bindingConstraints,
            BlocksNewEntries = blockingReasons.Count > 0,
            BlockingReasons = blockingReasons,
            RequiresManualReset = drawdownState.RequiresManualReset,
        };
    }
}

public sealed record PortfolioRiskGateResult
{
    public required decimal DrawdownBasedDeployablePercent { get; init; }
    public required decimal WeeklyBasedDeployablePercent { get; init; }
    public required decimal MonthlyBasedDeployablePercent { get; init; }

    /// <summary>MIN of the three candidates above — the drawdown/loss-limit axis's contribution to PositionSizingInputs.DeployableCapitalPercent (see class remarks: still needs combining with the regime axis by the caller).</summary>
    public required decimal DeployableCapitalPercent { get; init; }

    /// <summary>Name(s) of whichever candidate(s) tied for the minimum — same "prove which constraint bound" pattern as PositionSizingResult.BindingConstraints (Tranche 8).</summary>
    public required IReadOnlyList<string> BindingConstraints { get; init; }

    public required bool BlocksNewEntries { get; init; }

    /// <summary>Empty when BlocksNewEntries is false. Contains "TradingShutdown" and/or "DailyLossLimitBreached" — the two spec §4.5 conditions that are full blocks rather than size reductions.</summary>
    public required IReadOnlyList<string> BlockingReasons { get; init; }

    public required bool RequiresManualReset { get; init; }
}
