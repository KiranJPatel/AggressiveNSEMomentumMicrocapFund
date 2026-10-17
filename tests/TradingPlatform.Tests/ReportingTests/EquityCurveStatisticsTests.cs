using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

/// <summary>Spec §4.5 drawdown / recovery duration and §8.2 worst year, recovery time and the MaxDD&gt;40% rejection.</summary>
public class EquityCurveStatisticsTests
{
    private static List<PortfolioSnapshot> Curve(DateOnly start, params decimal[] navs) =>
        navs.Select((n, i) => new PortfolioSnapshot { AsOfDate = start.AddDays(i), Nav = n }).ToList();

    [Fact]
    public void DrawdownTiming_FindsPeakTroughAndRecoveryDates_OfTheDeepestEpisode()
    {
        // day0 100, day1 110 (peak), day2 99 (-10%), day3 88 (-20% trough), day4 105, day5 111 (recovers the 110 peak), day6 90 (new, shallower dip)
        var t = EquityCurveStatistics.ComputeDrawdownTiming(Curve(new DateOnly(2024, 1, 1), 100m, 110m, 99m, 88m, 105m, 111m, 90m));

        Assert.Equal(new DateOnly(2024, 1, 2), t.PeakDate);
        Assert.Equal(new DateOnly(2024, 1, 4), t.TroughDate);
        Assert.Equal(new DateOnly(2024, 1, 6), t.RecoveryDate);
        Assert.Equal(2, t.PeakToTroughDays);
        Assert.Equal(2, t.TroughToRecoveryDays);
        Assert.Equal(4, t.LongestUnderwaterDays);   // peak day1 -> regained day5
        Assert.True(t.StillUnderwater, "the series ends at 90 below the 111 high");
    }

    [Fact]
    public void DrawdownTiming_NeverRecovered_HasNoRecoveryDate_AndRunsUnderwaterToTheEnd()
    {
        var t = EquityCurveStatistics.ComputeDrawdownTiming(Curve(new DateOnly(2024, 1, 1), 100m, 120m, 90m, 95m, 100m));

        Assert.Null(t.RecoveryDate);
        Assert.Null(t.TroughToRecoveryDays);
        Assert.Equal(3, t.LongestUnderwaterDays);   // from the day-1 peak to the last day (day 4)
        Assert.True(t.StillUnderwater);
    }

    [Fact]
    public void DrawdownTiming_MonotonicRise_HasNoDrawdown()
    {
        var t = EquityCurveStatistics.ComputeDrawdownTiming(Curve(new DateOnly(2024, 1, 1), 100m, 101m, 105m));

        Assert.Null(t.PeakDate);
        Assert.Equal(0, t.LongestUnderwaterDays);
        Assert.False(t.StillUnderwater);
    }

    [Fact]
    public void DrawdownTiming_EmptySeries_IsAllNull()
    {
        var t = EquityCurveStatistics.ComputeDrawdownTiming(Array.Empty<PortfolioSnapshot>());

        Assert.Null(t.PeakDate);
        Assert.Equal(0, t.LongestUnderwaterDays);
    }

    [Fact]
    public void YearReturns_ChainFromThePriorYearEnd_AndFlagPartialYears()
    {
        var snaps = new List<PortfolioSnapshot>
        {
            new() { AsOfDate = new DateOnly(2022, 6, 1), Nav = 1_050_000m },   // first year starts mid-year: partial
            new() { AsOfDate = new DateOnly(2022, 12, 30), Nav = 1_100_000m },
            new() { AsOfDate = new DateOnly(2023, 1, 2), Nav = 1_000_000m },
            new() { AsOfDate = new DateOnly(2023, 12, 29), Nav = 880_000m },   // full year: 1.10M -> 0.88M = -20%
            new() { AsOfDate = new DateOnly(2024, 1, 2), Nav = 900_000m },
            new() { AsOfDate = new DateOnly(2024, 3, 1), Nav = 990_000m },     // last year ends in March: partial
        };

        var y = EquityCurveStatistics.ComputeYearReturns(snaps, 1_000_000m);

        Assert.Equal(3, y.Count);
        Assert.Equal(10m, y[0].ReturnPercent);  Assert.True(y[0].Partial);
        Assert.Equal(-20m, y[1].ReturnPercent); Assert.False(y[1].Partial);
        Assert.Equal(12.5m, y[2].ReturnPercent); Assert.True(y[2].Partial);
    }

    [Fact]
    public void Report_RejectsADrawdownAboveTheCeiling_EvenWithAHugeCagr()
    {
        var result = SelectionFixture.MakeResult(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1), new List<Trade>(), cagr: 85m, maxDrawdownPercent: 47m);

        var report = BacktestReport.Build(result, 1_000_000m, maxAcceptableDrawdownPercent: 40m);

        Assert.True(report.Rejected);
        Assert.True(report.VerdictNotes!.Any(n => n.StartsWith("REJECTED") && n.Contains("47.0%")));
        Assert.True(report.ToConsoleText().Contains("[VERDICT] REJECTED"));
    }

    [Fact]
    public void Report_WithinTheCeiling_IsNotRejected_ButAlwaysSaysOutOfSampleIsUnassessed()
    {
        var result = SelectionFixture.MakeResult(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1), new List<Trade>(), cagr: 25m, maxDrawdownPercent: 22m);

        var report = BacktestReport.Build(result, 1_000_000m);

        Assert.False(report.Rejected);
        Assert.True(report.VerdictNotes!.Any(n => n.Contains("walkforward")));
        Assert.True(report.ToCsv().Contains("Rejected"));
        Assert.True(report.ToHtml().Contains("Note"));
    }

    [Fact]
    public void Report_WarnsWhenThereAreTooFewTradesToTrustTheStatistics_AndStaysQuietOtherwise()
    {
        var few = Enumerable.Range(0, 8).Select(i => SelectionFixture.MakeTrade("S" + i, 100m)).ToList();
        var enough = Enumerable.Range(0, 40).Select(i => SelectionFixture.MakeTrade("S" + i, 100m)).ToList();

        var fewReport = BacktestReport.Build(SelectionFixture.MakeResult(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1), few, cagr: 20m), 1_000_000m);
        var enoughReport = BacktestReport.Build(SelectionFixture.MakeResult(new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1), enough, cagr: 20m), 1_000_000m);

        Assert.True(fewReport.VerdictNotes!.Any(n => n.Contains("only 8 trade")));
        Assert.False(enoughReport.VerdictNotes!.Any(n => n.Contains("only")));
        Assert.True(fewReport.ToConsoleText().Contains("only 8 trade"));
    }
}
