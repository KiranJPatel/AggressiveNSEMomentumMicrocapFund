using TradingPlatform.Core.Configuration;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class PeriodicLossLimitEngineTests
{
    private static readonly RiskOptions Options = new()
    {
        DailyLossLimitPercent = 2m,
        WeeklyLossLimitPercent = 4m,
        MonthlyLossLimitPercent = 8m,
    };

    // 2026-01-05 is a Monday.
    private static DateOnly Monday => new(2026, 1, 5);

    [Fact]
    public void FirstObservation_AnchorsAllThreePeriods_AtCurrentNav_ZeroLoss()
    {
        var state = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);

        Assert.Equal(1_000_000m, state.DailyAnchorNav);
        Assert.Equal(1_000_000m, state.WeeklyAnchorNav);
        Assert.Equal(1_000_000m, state.MonthlyAnchorNav);
        Assert.Equal(0m, state.DailyLossPercent);
        Assert.False(state.DailyLimitBreached);
        Assert.False(state.WeeklyLimitBreached);
        Assert.False(state.MonthlyLimitBreached);
    }

    [Fact]
    public void SameDay_RepeatedCalls_DailyAnchorStaysAtMorningNav()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        var s2 = PeriodicLossLimitEngine.Evaluate(s1, Monday, 970_000m, Options); // same day, later mark

        Assert.Equal(1_000_000m, s2.DailyAnchorNav); // unchanged — still this morning's NAV
        Assert.Equal(3m, s2.DailyLossPercent);
    }

    [Fact]
    public void NewDay_ResetsDailyAnchor_ButNotWeeklyOrMonthly()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        var s2 = PeriodicLossLimitEngine.Evaluate(s1, Monday.AddDays(1), 970_000m, Options); // Tuesday, new day, same week/month

        Assert.Equal(970_000m, s2.DailyAnchorNav); // re-anchored to Tuesday's opening mark
        Assert.Equal(0m, s2.DailyLossPercent);      // no loss yet today
        Assert.Equal(1_000_000m, s2.WeeklyAnchorNav);  // weekly anchor untouched
        Assert.Equal(1_000_000m, s2.MonthlyAnchorNav); // monthly anchor untouched
        Assert.Equal(3m, s2.WeeklyLossPercent);         // still measured against Monday's weekly open
    }

    [Fact]
    public void DailyLossBreach_TriggersAtOrAboveTwoPercent()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        var justUnder = PeriodicLossLimitEngine.Evaluate(s1, Monday, 981_000m, Options); // 1.9% down
        var justAtLimit = PeriodicLossLimitEngine.Evaluate(s1, Monday, 980_000m, Options); // exactly 2.0% down

        Assert.False(justUnder.DailyLimitBreached);
        Assert.True(justAtLimit.DailyLimitBreached);
    }

    [Fact]
    public void WeeklyAnchor_ResetsOnNewIsoWeek_Monday()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        var friday = PeriodicLossLimitEngine.Evaluate(s1, Monday.AddDays(4), 950_000m, Options); // same week, 5% down
        var nextMonday = PeriodicLossLimitEngine.Evaluate(friday, Monday.AddDays(7), 950_000m, Options); // new week

        Assert.True(friday.WeeklyLimitBreached); // 5% >= 4% weekly limit
        Assert.Equal(950_000m, nextMonday.WeeklyAnchorNav); // re-anchored on the new week
        Assert.Equal(0m, nextMonday.WeeklyLossPercent);
        Assert.False(nextMonday.WeeklyLimitBreached);
    }

    [Fact]
    public void MonthlyAnchor_ResetsOnNewCalendarMonth()
    {
        var jan15 = new DateOnly(2026, 1, 15);
        var feb1 = new DateOnly(2026, 2, 1);

        var s1 = PeriodicLossLimitEngine.Evaluate(null, jan15, 1_000_000m, Options);
        var lateJan = PeriodicLossLimitEngine.Evaluate(s1, new DateOnly(2026, 1, 28), 900_000m, Options); // -10%, breach
        var newMonth = PeriodicLossLimitEngine.Evaluate(lateJan, feb1, 900_000m, Options);

        Assert.True(lateJan.MonthlyLimitBreached);
        Assert.Equal(900_000m, newMonth.MonthlyAnchorNav);
        Assert.False(newMonth.MonthlyLimitBreached);
    }

    [Fact]
    public void Gain_NeverProducesNegativeLossPercent()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        var s2 = PeriodicLossLimitEngine.Evaluate(s1, Monday, 1_050_000m, Options); // up 5%

        Assert.Equal(0m, s2.DailyLossPercent);
        Assert.False(s2.DailyLimitBreached);
    }

    [Fact]
    public void OutOfOrderDate_Throws()
    {
        var s1 = PeriodicLossLimitEngine.Evaluate(null, Monday, 1_000_000m, Options);
        Assert.Throws<ArgumentException>(() => PeriodicLossLimitEngine.Evaluate(s1, Monday.AddDays(-1), 900_000m, Options));
    }
}
