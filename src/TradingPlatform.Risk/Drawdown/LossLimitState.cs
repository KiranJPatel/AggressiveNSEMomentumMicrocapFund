namespace TradingPlatform.Risk.Drawdown;

/// <summary>
/// Running daily/weekly/monthly loss-limit state (spec §4.5). Each period
/// anchors to the NAV as of the first observation seen within that period
/// (the first call on a new calendar day / ISO week / calendar month) —
/// the same "first observation of a fresh episode" pattern DrawdownEngine
/// uses for episode starts. Threaded call-to-call the same way, by the
/// same explicit-state-in/out convention as every other engine in this
/// codebase (TrailingStopCalculator, DrawdownEngine).
///
/// Weeks are ISO (Monday-start) — the NSE trading week runs Mon-Fri
/// anyway, so this never actually straddles a period boundary mid-week.
/// </summary>
public sealed record LossLimitState
{
    public required DateOnly AsOfDate { get; init; }
    public required decimal CurrentNav { get; init; }

    public required DateOnly DailyAnchorDate { get; init; }
    public required decimal DailyAnchorNav { get; init; }
    public required decimal DailyLossPercent { get; init; }
    public required bool DailyLimitBreached { get; init; }

    public required DateOnly WeeklyAnchorWeekStart { get; init; }
    public required decimal WeeklyAnchorNav { get; init; }
    public required decimal WeeklyLossPercent { get; init; }
    public required bool WeeklyLimitBreached { get; init; }

    public required DateOnly MonthlyAnchorMonthStart { get; init; }
    public required decimal MonthlyAnchorNav { get; init; }
    public required decimal MonthlyLossPercent { get; init; }
    public required bool MonthlyLimitBreached { get; init; }
}
