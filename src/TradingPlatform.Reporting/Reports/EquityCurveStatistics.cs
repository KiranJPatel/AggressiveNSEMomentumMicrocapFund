using TradingPlatform.Core.Models;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Timing of the deepest drawdown episode and of the longest stretch spent below a prior NAV peak (spec §4.5 Drawdown Duration / Recovery Duration, §8.2 recovery time).</summary>
public sealed record DrawdownTiming(
    DateOnly? PeakDate, DateOnly? TroughDate, DateOnly? RecoveryDate,
    int? PeakToTroughDays, int? TroughToRecoveryDays, int LongestUnderwaterDays, bool StillUnderwater);

/// <summary>Calendar-year return from the daily NAV series. A year the run covers only partly is flagged, not silently compared with full years.</summary>
public sealed record YearReturn(int Year, decimal ReturnPercent, bool Partial);

public static class EquityCurveStatistics
{
    public static DrawdownTiming ComputeDrawdownTiming(IReadOnlyList<PortfolioSnapshot> snapshots)
    {
        var series = snapshots.OrderBy(s => s.AsOfDate).ToList();
        if (series.Count == 0) return new DrawdownTiming(null, null, null, null, null, 0, false);

        var peakNav = series[0].Nav;
        var peakDate = series[0].AsOfDate;
        decimal worstDrawdown = 0m;
        DateOnly? worstPeakDate = null, worstTroughDate = null;
        decimal worstPeakNav = 0m;
        var longestUnderwater = 0;
        var wasUnderwater = false;   // only a stretch that actually dipped below the peak counts; a plain new high is not "recovery"

        foreach (var s in series)
        {
            if (s.Nav >= peakNav)
            {
                // Back at (or above) the old peak: if we had been below it, that underwater stretch ends here.
                if (wasUnderwater) longestUnderwater = Math.Max(longestUnderwater, s.AsOfDate.DayNumber - peakDate.DayNumber);
                wasUnderwater = false;
                peakNav = s.Nav;
                peakDate = s.AsOfDate;
                continue;
            }
            wasUnderwater = true;
            var drawdown = peakNav > 0m ? (peakNav - s.Nav) / peakNav * 100m : 0m;
            if (drawdown > worstDrawdown)
            {
                worstDrawdown = drawdown;
                worstPeakDate = peakDate;
                worstTroughDate = s.AsOfDate;
                worstPeakNav = peakNav;
            }
        }

        var last = series[^1];
        var stillUnderwater = last.Nav < peakNav;
        if (stillUnderwater) longestUnderwater = Math.Max(longestUnderwater, last.AsOfDate.DayNumber - peakDate.DayNumber);

        if (worstPeakDate is null) return new DrawdownTiming(null, null, null, null, null, longestUnderwater, stillUnderwater);

        DateOnly? recovery = series.Where(s => s.AsOfDate > worstTroughDate!.Value && s.Nav >= worstPeakNav).Select(s => (DateOnly?)s.AsOfDate).FirstOrDefault();
        return new DrawdownTiming(
            worstPeakDate, worstTroughDate, recovery,
            worstTroughDate!.Value.DayNumber - worstPeakDate.Value.DayNumber,
            recovery is { } r ? r.DayNumber - worstTroughDate.Value.DayNumber : null,
            longestUnderwater, stillUnderwater);
    }

    /// <summary>Each year's return = its last NAV vs the previous year's last NAV (the first year starts from <paramref name="initialCapital"/>).</summary>
    public static IReadOnlyList<YearReturn> ComputeYearReturns(IReadOnlyList<PortfolioSnapshot> snapshots, decimal initialCapital)
    {
        if (snapshots.Count == 0) return Array.Empty<YearReturn>();
        var byYear = snapshots.GroupBy(s => s.AsOfDate.Year).OrderBy(g => g.Key).ToList();
        var firstDate = snapshots.Min(s => s.AsOfDate);
        var lastDate = snapshots.Max(s => s.AsOfDate);

        var result = new List<YearReturn>();
        var prior = initialCapital;
        foreach (var year in byYear)
        {
            var end = year.OrderBy(s => s.AsOfDate).Last().Nav;
            var partial = (year.Key == firstDate.Year && firstDate.Month > 1) || (year.Key == lastDate.Year && lastDate.Month < 12);
            if (prior > 0m) result.Add(new YearReturn(year.Key, (end - prior) / prior * 100m, partial));
            prior = end;
        }
        return result;
    }
}
