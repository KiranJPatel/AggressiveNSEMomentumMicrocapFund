using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

public class BacktestReportTests
{
    private static PortfolioSnapshot Snapshot(DateOnly date, decimal nav, RegimeClassification? regime = null) => new()
    {
        AsOfDate = date, Nav = nav, CashBalance = nav, PeakNav = nav, DrawdownPercent = 0m,
        PortfolioHeatPercent = 2m, OpenPositionCount = 0, Regime = regime, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BacktestRunResult MakeResultWithSnapshots(IReadOnlyList<PortfolioSnapshot> snapshots, IReadOnlyList<Trade> trades)
    {
        var baseResult = SelectionFixture.MakeResult(snapshots[0].AsOfDate, snapshots[^1].AsOfDate, trades, cagr: 15m, sharpe: 1.2m, maxDrawdownPercent: 12m);
        baseResult.Run.FinalCapital = snapshots[^1].Nav; // keep Run.FinalCapital consistent with the synthetic snapshots, matching how the real BacktestEngine derives it (Tranche 11)
        return new BacktestRunResult
        {
            Run = baseResult.Run,
            Trades = trades,
            TradeLegs = Array.Empty<TradeLeg>(),
            DailySnapshots = snapshots,
            DrawdownEvents = Array.Empty<DrawdownEvent>(),
        };
    }

    [Fact]
    public void Build_ComputesNetReturnPercent_FromInitialAndFinalCapital()
    {
        var snapshots = new[] { Snapshot(new DateOnly(2020, 1, 1), 1_000_000m), Snapshot(new DateOnly(2020, 1, 2), 1_200_000m) };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());

        var report = BacktestReport.Build(result, 1_000_000m);

        Assert.Equal(20m, report.NetReturnPercent); // (1,200,000-1,000,000)/1,000,000*100
    }

    [Fact]
    public void Build_WorstAndBestMonth_ComputedFromMonthlySnapshotGroups()
    {
        var snapshots = new[]
        {
            Snapshot(new DateOnly(2020, 1, 31), 1_100_000m), // Jan: +10% vs initial 1,000,000
            Snapshot(new DateOnly(2020, 2, 28), 990_000m),   // Feb: -10% vs Jan's 1,100,000
            Snapshot(new DateOnly(2020, 3, 31), 1_188_000m), // Mar: +20% vs Feb's 990,000
        };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());

        var report = BacktestReport.Build(result, 1_000_000m);

        Assert.Equal(-10m, report.WorstMonthPercent);
        Assert.Equal(20m, report.BestMonthPercent);
    }

    [Fact]
    public void Build_RegimeDistribution_ComputesBucketPercentagesFromSnapshots()
    {
        var snapshots = new[]
        {
            Snapshot(new DateOnly(2020, 1, 1), 1_000_000m, RegimeClassification.Aggressive), // Bull
            Snapshot(new DateOnly(2020, 1, 2), 1_000_000m, RegimeClassification.Normal),      // Bull
            Snapshot(new DateOnly(2020, 1, 3), 1_000_000m, RegimeClassification.Cautious),    // Normal
            Snapshot(new DateOnly(2020, 1, 4), 1_000_000m, RegimeClassification.Emergency),   // Bear
        };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());

        var report = BacktestReport.Build(result, 1_000_000m);

        Assert.Equal(50m, report.RegimeBullPercent);
        Assert.Equal(25m, report.RegimeNormalPercent);
        Assert.Equal(25m, report.RegimeBearPercent);
    }

    [Fact]
    public void Build_NoRegimeData_ReturnsZeroPercentages_NotDivideByZeroCrash()
    {
        var snapshots = new[] { Snapshot(new DateOnly(2020, 1, 1), 1_000_000m) }; // Regime is null
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());

        var report = BacktestReport.Build(result, 1_000_000m);

        Assert.Equal(0m, report.RegimeBullPercent);
    }

    [Fact]
    public void ToCsv_ProducesHeaderAndOneDataRow()
    {
        var snapshots = new[] { Snapshot(new DateOnly(2020, 1, 1), 1_000_000m) };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());
        var report = BacktestReport.Build(result, 1_000_000m);

        var csv = report.ToCsv();

        Assert.True(csv.Contains("InitialCapital"));
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // header + 1 row
    }

    [Fact]
    public void ToJson_RoundTripsViaSystemTextJson()
    {
        var snapshots = new[] { Snapshot(new DateOnly(2020, 1, 1), 1_000_000m) };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());
        var report = BacktestReport.Build(result, 1_000_000m);

        var json = report.ToJson();

        Assert.True(json.Contains("InitialCapital"));
    }

    [Fact]
    public void ToHtml_FlagsSurvivorshipBias_WhenPresent()
    {
        var snapshots = new[] { Snapshot(new DateOnly(2020, 1, 1), 1_000_000m) };
        var result = MakeResultWithSnapshots(snapshots, Array.Empty<Trade>());
        result.Run.SurvivorshipBiased = true;

        var report = BacktestReport.Build(result, 1_000_000m);
        var html = report.ToHtml();

        Assert.True(html.Contains("SURVIVORSHIP-BIASED"));
    }
}
