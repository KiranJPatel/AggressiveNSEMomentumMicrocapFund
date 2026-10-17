using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Structure;
using TradingPlatform.Indicators.Volatility;

namespace TradingPlatform.Risk.Trailing;

/// <summary>
/// Implements spec §4.3's trailing-stop ladder literally:
///   Initial stop -> Breakeven -> ATR trailing -> Swing-low trailing -> HMA/Supertrend trailing
///   +1R -> breakeven, +2R -> active (ATR) trailing, +3R -> aggressive winner protection
///
/// Five named stages but only three numeric triggers — spec's "+3R ->
/// aggressive winner protection" covers BOTH the swing-low and HMA/
/// Supertrend methods together as one final, tightest tier (a resolved
/// ambiguity, documented here): at +3R, this calculator computes all three
/// candidate stops still available (ATR, swing-low, Supertrend) and uses
/// whichever is TIGHTEST (highest, closest to price) — "aggressive winner
/// protection" means locking in the most gain the currently-available
/// signals support, not picking arbitrarily among them.
///
/// The one invariant that overrides every stage: the stop only ever
/// RATCHETS UP. NewStop = max(CurrentStop, candidate) always — a
/// deteriorating candidate (e.g. ATR widening after a volatility spike)
/// can never pull the stop back down.
/// </summary>
public static class TrailingStopCalculator
{
    public static TrailingStopResult Calculate(
        Position position,
        IReadOnlyList<HistoricalBar> barsUpToNow,
        IndicatorSnapshot currentIndicators,
        StrategyOptions strategyOptions,
        ExitOptions exitOptions)
    {
        if (barsUpToNow.Count == 0) throw new ArgumentException("At least one bar is required.");
        if (position.RiskPerShare <= 0) throw new ArgumentException("Position.RiskPerShare must be positive to compute an R-multiple.");

        var currentPrice = barsUpToNow[^1].Close;
        var rMultiple = (currentPrice - position.AverageEntryPrice) / position.RiskPerShare;

        var (stage, candidateStop) = rMultiple switch
        {
            _ when rMultiple >= strategyOptions.TrailingAggressiveTriggerR =>
                (TrailingStage.AggressiveWinnerProtection, AggressiveCandidateStop(barsUpToNow, currentIndicators, currentPrice, exitOptions)),

            _ when rMultiple >= strategyOptions.TrailingActiveTriggerR =>
                (TrailingStage.AtrTrailing, AtrCandidateStop(barsUpToNow, currentPrice, exitOptions)),

            _ when rMultiple >= strategyOptions.TrailingBreakevenTriggerR =>
                (TrailingStage.Breakeven, position.AverageEntryPrice),

            _ => (TrailingStage.Initial, position.CurrentStop),
        };

        var newStop = Math.Max(position.CurrentStop, candidateStop);
        return new TrailingStopResult(stage, newStop, rMultiple);
    }

    private static decimal AtrCandidateStop(IReadOnlyList<HistoricalBar> bars, decimal currentPrice, ExitOptions options)
    {
        var atr = AverageTrueRange.Calculate(bars, options.TrailingAtrPeriod);
        var atrValue = atr[^1];
        return atrValue is { } v ? currentPrice - options.TrailingAtrMultiplier * v : decimal.MinValue; // unavailable -> never wins a max()
    }

    private static decimal AggressiveCandidateStop(IReadOnlyList<HistoricalBar> bars, IndicatorSnapshot indicators, decimal currentPrice, ExitOptions options)
    {
        var atrStop = AtrCandidateStop(bars, currentPrice, options);

        var swingPoints = SwingPointDetector.Detect(bars, options.TrailingSwingLookback);
        var mostRecentSwingLow = swingPoints
            .Where(p => p.Type == SwingPointType.Low)
            .OrderByDescending(p => p.BarIndex)
            .FirstOrDefault();
        var swingLowStop = mostRecentSwingLow?.Price ?? decimal.MinValue;

        var supertrendStop = indicators.Supertrend ?? decimal.MinValue;

        return Math.Max(atrStop, Math.Max(swingLowStop, supertrendStop));
    }
}
