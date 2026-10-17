using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Risk.Drawdown;

/// <summary>
/// Implements spec §4.5's daily/weekly/monthly loss-limit tracking:
/// "Daily loss limit 2% -> stop new trades that day; Weekly loss limit 4%
/// -> reduce risk; Monthly loss limit 8% -> defensive mode."
///
/// Deliberately separate from DrawdownEngine even though both watch NAV,
/// because they measure genuinely different things: DrawdownEngine tracks
/// distance from the portfolio's all-time peak (an unbounded-lookback,
/// single running high-water mark); this engine tracks loss from a
/// calendar-period anchor that resets on a fixed schedule (today's open,
/// this week's open, this month's open) regardless of where the all-time
/// peak sits. A portfolio can be down 1% from its all-time peak (no
/// drawdown-tier concern at all) while still down 3% since this morning
/// (a daily-limit breach) — the two axes are independent and both matter.
/// </summary>
public static class PeriodicLossLimitEngine
{
    public static LossLimitState Evaluate(LossLimitState? previous, DateOnly asOfDate, decimal currentNav, RiskOptions options)
    {
        if (currentNav <= 0) throw new ArgumentException("currentNav must be positive.", nameof(currentNav));
        if (previous is not null && asOfDate < previous.AsOfDate)
            throw new ArgumentException($"asOfDate ({asOfDate}) must not be before the previous observation ({previous.AsOfDate}).", nameof(asOfDate));

        var weekStart = WeekStart(asOfDate);
        var monthStart = new DateOnly(asOfDate.Year, asOfDate.Month, 1);

        var (dailyAnchorDate, dailyAnchorNav) = previous is null || previous.DailyAnchorDate != asOfDate
            ? (asOfDate, currentNav)
            : (previous.DailyAnchorDate, previous.DailyAnchorNav);

        var (weeklyAnchorWeekStart, weeklyAnchorNav) = previous is null || previous.WeeklyAnchorWeekStart != weekStart
            ? (weekStart, currentNav)
            : (previous.WeeklyAnchorWeekStart, previous.WeeklyAnchorNav);

        var (monthlyAnchorMonthStart, monthlyAnchorNav) = previous is null || previous.MonthlyAnchorMonthStart != monthStart
            ? (monthStart, currentNav)
            : (previous.MonthlyAnchorMonthStart, previous.MonthlyAnchorNav);

        var dailyLossPercent = LossPercent(dailyAnchorNav, currentNav);
        var weeklyLossPercent = LossPercent(weeklyAnchorNav, currentNav);
        var monthlyLossPercent = LossPercent(monthlyAnchorNav, currentNav);

        return new LossLimitState
        {
            AsOfDate = asOfDate,
            CurrentNav = currentNav,

            DailyAnchorDate = dailyAnchorDate,
            DailyAnchorNav = dailyAnchorNav,
            DailyLossPercent = dailyLossPercent,
            DailyLimitBreached = dailyLossPercent >= options.DailyLossLimitPercent,

            WeeklyAnchorWeekStart = weeklyAnchorWeekStart,
            WeeklyAnchorNav = weeklyAnchorNav,
            WeeklyLossPercent = weeklyLossPercent,
            WeeklyLimitBreached = weeklyLossPercent >= options.WeeklyLossLimitPercent,

            MonthlyAnchorMonthStart = monthlyAnchorMonthStart,
            MonthlyAnchorNav = monthlyAnchorNav,
            MonthlyLossPercent = monthlyLossPercent,
            MonthlyLimitBreached = monthlyLossPercent >= options.MonthlyLossLimitPercent,
        };
    }

    /// <summary>Loss since the anchor, floored at 0 (a gain is never a negative "loss").</summary>
    private static decimal LossPercent(decimal anchorNav, decimal currentNav)
    {
        if (anchorNav <= 0) return 0m; // defensive only; Nav is never legitimately <= 0 in practice
        return Math.Max(0m, (anchorNav - currentNav) / anchorNav * 100m);
    }

    /// <summary>ISO (Monday-start) week containing the given date.</summary>
    private static DateOnly WeekStart(DateOnly date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }
}
