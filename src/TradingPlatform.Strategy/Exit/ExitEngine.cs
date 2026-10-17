using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Exit;

/// <summary>
/// Implements spec §4.3's four independent exit mechanisms as an OR-gate —
/// unlike Breakout's AND-gate, any ONE mechanism firing means exit:
///
///  (A) StopLoss — immediate. Checked against the bar's LOW, not Close:
///      a stop-loss is fundamentally different from an entry signal (spec
///      §3.8's "wait for confirmed close" convention is about avoiding
///      false entry signals, not about how a protective stop behaves — a
///      real stop-loss order fires the instant price touches it,
///      intraday, not at end-of-day confirmation).
///  (B) MomentumFailure — RS deterioration AND HMA breakdown AND volume
///      distribution, all three required together (spec's literal "+"
///      phrasing reads as a conjunction, not "any one of these").
///      "Volume distribution" here means elevated volume on an intraday
///      down day (Close &lt; Open) — a single-bar-observable proxy for the
///      classic "distribution day" pattern, since this evaluator only
///      receives the current bar, not a window.
///  (C) FundamentalFailure — only evaluated where fundamental data is
///      genuinely Available (spec: "where fundamental data is available");
///      absent data means this mechanism simply doesn't fire, never a
///      fabricated trigger OR a fabricated pass.
///  (D) RegimeFailure — resolved here as "current regime is Emergency",
///      the same threshold BreakoutEngine uses to block new entries
///      (symmetric treatment: Emergency blocks entries AND signals exit
///      of existing positions). Finer-grained "trim only the weakest
///      positions" partial-exposure-reduction logic is a portfolio-level
///      concern for a later orchestration tranche to build on top of this
///      per-position signal, not something a single position's exit
///      evaluation can decide on its own.
///
/// Never a fixed-percentage take-profit exit — spec §4.3 explicitly:
/// "Never auto-take-profit at a fixed +10%/+20%. Let winners run." There
/// is deliberately no such mechanism here at all, not even as a disabled
/// option.
/// </summary>
public static class ExitEngine
{
    public static ExitEvaluation Evaluate(
        Position position,
        HistoricalBar currentBar,
        IndicatorSnapshot currentIndicators,
        decimal relativeStrengthScore,
        RegimeClassification regime,
        FundamentalSnapshot? currentFundamentals,
        ExitOptions options)
    {
        var mechanisms = new List<ExitMechanismResult>
        {
            EvaluateStopLoss(position, currentBar),
            EvaluateMomentumFailure(currentBar, currentIndicators, relativeStrengthScore, options),
            EvaluateFundamentalFailure(currentFundamentals, options),
            EvaluateRegimeFailure(regime),
        };

        var fired = mechanisms.Where(m => m.Fired).ToList();
        var primary = fired.Count > 0 ? fired[0].Reason : (ExitReason?)null; // list is already in A>B>C>D priority order

        return new ExitEvaluation(fired.Count > 0, primary, mechanisms);
    }

    private static ExitMechanismResult EvaluateStopLoss(Position position, HistoricalBar currentBar)
    {
        var fired = currentBar.Low <= position.CurrentStop;
        return new ExitMechanismResult(ExitReason.StopLoss, fired,
            $"Bar low {currentBar.Low} {(fired ? "<=" : ">")} stop {position.CurrentStop}");
    }

    private static ExitMechanismResult EvaluateMomentumFailure(HistoricalBar currentBar, IndicatorSnapshot indicators, decimal relativeStrengthScore, ExitOptions options)
    {
        var rsDeteriorated = relativeStrengthScore < options.MomentumFailureRsThreshold;

        var hmaBreakdown = indicators.HmaFast is { } fast && indicators.HmaSlow is { } slow
            && currentBar.Close < fast && fast < slow;

        var distributionDay = indicators.RelativeVolume is { } relVol
            && relVol >= options.DistributionVolumeMultiplier
            && currentBar.Close < currentBar.Open;

        var fired = rsDeteriorated && hmaBreakdown && distributionDay;
        return new ExitMechanismResult(ExitReason.MomentumFailure, fired,
            $"RS deteriorated={rsDeteriorated} (score {relativeStrengthScore} vs threshold {options.MomentumFailureRsThreshold}), HmaBreakdown={hmaBreakdown}, DistributionDay={distributionDay}");
    }

    private static ExitMechanismResult EvaluateFundamentalFailure(FundamentalSnapshot? fundamentals, ExitOptions options)
    {
        if (fundamentals is null)
            return new ExitMechanismResult(ExitReason.FundamentalFailure, false, "No fundamental snapshot supplied — mechanism not evaluated.");

        var earningsSevere = fundamentals.EarningsGrowthAvailability == DataAvailability.Available
            && fundamentals.EarningsGrowthPercent is { } eg && eg < options.SevereFundamentalDeclinePercent;

        var revenueSevere = fundamentals.RevenueGrowthAvailability == DataAvailability.Available
            && fundamentals.RevenueGrowthPercent is { } rg && rg < options.SevereFundamentalDeclinePercent;

        var fired = earningsSevere || revenueSevere;
        return new ExitMechanismResult(ExitReason.FundamentalFailure, fired,
            fired ? $"Severe fundamental decline detected (threshold {options.SevereFundamentalDeclinePercent}%)" : "No severe fundamental decline detected (or data unavailable).");
    }

    private static ExitMechanismResult EvaluateRegimeFailure(RegimeClassification regime)
    {
        var fired = regime == RegimeClassification.Emergency;
        return new ExitMechanismResult(ExitReason.RegimeFailure, fired, $"Regime classification: {regime}");
    }
}
