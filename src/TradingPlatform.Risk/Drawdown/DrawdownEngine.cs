using TradingPlatform.Core.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Risk.Drawdown;

/// <summary>
/// Implements spec §4.5's drawdown tracking and circuit-breaker tiers.
///
/// Pure function of (previous state, new NAV observation) -> next state,
/// the same explicit-state-in/out shape as TrailingStopCalculator
/// (Tranche 9). This engine owns none of the eight DrawdownTier thresholds
/// or percentages themselves — those were already fully specified,
/// numerically, in RiskOptions back in Tranche 0 (spec §4.5's table has no
/// ambiguity to resolve, unlike the weekly/monthly loss-limit magnitudes —
/// see PeriodicLossLimitEngine). This engine's job is purely the
/// peak-tracking arithmetic and the tier lookup.
///
/// Manual-reset latch (spec §4.5: "40% TRADING SHUTDOWN — no new trades,
/// explicit manual reset required"): once the shutdown tier is reached,
/// DrawdownState.RequiresManualReset latches true and stays true across
/// every subsequent Evaluate call — even after NAV fully recovers — until
/// the caller explicitly passes manualResetAcknowledged: true. A reset
/// attempt made while the drawdown is STILL at/above the shutdown
/// threshold is refused (the latch cannot be cleared while genuinely still
/// in a 40%+ drawdown; the whole point is that recovery alone is never
/// sufficient) — a resolved ambiguity, spec §0.4, since the spec doesn't
/// spell out what "explicit manual reset" should refuse.
/// </summary>
public static class DrawdownEngine
{
    public static (DrawdownState State, DrawdownEvent? Event) Evaluate(
        DrawdownState? previous,
        DateOnly asOfDate,
        decimal currentNav,
        RiskOptions options,
        bool manualResetAcknowledged = false)
    {
        if (currentNav <= 0) throw new ArgumentException("currentNav must be positive.", nameof(currentNav));

        var tiers = EffectiveTiers(options);

        if (previous is null)
        {
            var initialTier = LookupTier(0m, tiers);
            var initialState = new DrawdownState
            {
                AsOfDate = asOfDate,
                CurrentNav = currentNav,
                PeakNav = currentNav,
                PeakDate = asOfDate,
                DrawdownPercent = 0m,
                DrawdownStartDate = null,
                DrawdownDurationDays = null,
                TroughDate = null,
                EpisodeTroughDrawdownPercent = null,
                MaxDrawdownPercent = 0m,
                MaxDrawdownDate = null,
                CurrentTier = initialTier,
                RequiresManualReset = false,
            };
            return (initialState, null);
        }

        if (asOfDate < previous.AsOfDate)
            throw new ArgumentException($"asOfDate ({asOfDate}) must not be before the previous observation ({previous.AsOfDate}).", nameof(asOfDate));

        decimal peakNav;
        DateOnly peakDate;
        decimal drawdownPercent;
        DateOnly? drawdownStartDate;
        int? drawdownDurationDays;
        DateOnly? troughDate;
        decimal? episodeTroughDrawdownPercent;
        int? lastRecoveryDurationDays = previous.LastRecoveryDurationDays;

        if (currentNav >= previous.PeakNav)
        {
            // New all-time high (or first bar at the existing peak) — the
            // episode, if any, is over. Measure how long recovery took
            // from this episode's deepest point, if there was one.
            peakNav = currentNav;
            peakDate = asOfDate;
            drawdownPercent = 0m;
            drawdownStartDate = null;
            drawdownDurationDays = null;

            if (previous.TroughDate is { } troughedAt)
            {
                lastRecoveryDurationDays = asOfDate.DayNumber - troughedAt.DayNumber;
            }

            troughDate = null;
            episodeTroughDrawdownPercent = null;
        }
        else
        {
            peakNav = previous.PeakNav;
            peakDate = previous.PeakDate;
            drawdownPercent = (peakNav - currentNav) / peakNav * 100m;

            drawdownStartDate = previous.DrawdownStartDate ?? asOfDate; // first underwater day of a fresh episode
            drawdownDurationDays = asOfDate.DayNumber - drawdownStartDate.Value.DayNumber;

            var previousEpisodeTrough = previous.EpisodeTroughDrawdownPercent ?? -1m; // -1 so day 1 of an episode always "wins"
            if (drawdownPercent >= previousEpisodeTrough)
            {
                troughDate = asOfDate;
                episodeTroughDrawdownPercent = drawdownPercent;
            }
            else
            {
                troughDate = previous.TroughDate;
                episodeTroughDrawdownPercent = previous.EpisodeTroughDrawdownPercent;
            }
        }

        var maxDrawdownPercent = previous.MaxDrawdownPercent;
        var maxDrawdownDate = previous.MaxDrawdownDate;
        if (drawdownPercent > maxDrawdownPercent)
        {
            maxDrawdownPercent = drawdownPercent;
            maxDrawdownDate = asOfDate;
        }

        var currentTier = LookupTier(drawdownPercent, tiers);

        var requiresManualReset = previous.RequiresManualReset;
        if (manualResetAcknowledged)
        {
            // Refused while still genuinely at/above the shutdown
            // threshold — recovery, not just acknowledgement, is required.
            requiresManualReset = currentTier.IsShutdown;
        }
        else if (currentTier.IsShutdown)
        {
            requiresManualReset = true; // latch engages (or stays engaged)
        }

        var state = new DrawdownState
        {
            AsOfDate = asOfDate,
            CurrentNav = currentNav,
            PeakNav = peakNav,
            PeakDate = peakDate,
            DrawdownPercent = drawdownPercent,
            DrawdownStartDate = drawdownStartDate,
            DrawdownDurationDays = drawdownDurationDays,
            TroughDate = troughDate,
            EpisodeTroughDrawdownPercent = episodeTroughDrawdownPercent,
            MaxDrawdownPercent = maxDrawdownPercent,
            MaxDrawdownDate = maxDrawdownDate,
            CurrentTier = currentTier,
            RequiresManualReset = requiresManualReset,
            LastRecoveryDurationDays = lastRecoveryDurationDays,
        };

        DrawdownEvent? tierEvent = null;
        if (currentTier.Label != previous.CurrentTier.Label)
        {
            tierEvent = new DrawdownEvent
            {
                DrawdownPercent = drawdownPercent,
                TierName = currentTier.Label,
                RiskReductionPercent = currentTier.RiskReductionPercent,
                TradingShutdown = state.IsTradingShutdown,
                Timestamp = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), IndiaStandardTime.Offset),
            };
        }

        return (state, tierEvent);
    }

    /// <summary>Finds the highest-threshold tier whose ThresholdPercent is at or below the given drawdown — e.g. exactly 10.0% selects the 10% tier, not the 0% one (spec §4.5's ranges read as [lower, upper)).</summary>
    private static DrawdownTier LookupTier(decimal drawdownPercent, IReadOnlyList<DrawdownTier> sortedAscendingTiers)
    {
        var selected = sortedAscendingTiers[0];
        foreach (var tier in sortedAscendingTiers)
        {
            if (tier.ThresholdPercent <= drawdownPercent) selected = tier;
            else break;
        }
        return selected;
    }

    private static IReadOnlyList<DrawdownTier> EffectiveTiers(RiskOptions options)
    {
        var tiers = options.DrawdownTiers.Count > 0 ? options.DrawdownTiers : RiskOptions.DefaultDrawdownTiers;
        return tiers.OrderBy(t => t.ThresholdPercent).ToList();
    }
}
