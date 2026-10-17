using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

public class ScannerReportTests
{
    private static ScannerResult Result(int rank, string symbol) => new()
    {
        ScanTimestamp = DateTimeOffset.UtcNow, Rank = rank, Symbol = symbol, Price = 100m, Score = 80m,
        RelativeStrengthScore = 75m, MomentumScore = 70m, VolumeScore = 60m, VcpScore = 50m,
        PivotPrice = 95m, BreakoutStatus = "Breakout confirmed", DistanceFromPivotPercent = 5m,
        SuggestedEntry = 95m, SuggestedStop = 85m, Risk = 1000m, SuggestedPositionSize = 100m,
    };

    [Fact]
    public void ToCsv_OneRowPerCandidate()
    {
        var report = ScannerReport.Build(new[] { Result(1, "ABC"), Result(2, "XYZ") });
        var csv = report.ToCsv();
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ToHtml_IncludesCandidateCountInTitle()
    {
        var report = ScannerReport.Build(new[] { Result(1, "ABC") });
        Assert.True(report.ToHtml().Contains("1 candidates"));
    }

    [Fact]
    public void ToCsv_MissingOptionalIndicators_RenderAsNotAvailable()
    {
        var result = Result(1, "ABC"); // Adx/Rsi left null
        var csv = ScannerReport.Build(new[] { result }).ToCsv();
        Assert.True(csv.Contains("n/a"));
    }
}

public class OptimizationReportTests
{
    [Fact]
    public void Build_Winner_IsFirstRow_NeverReSorted()
    {
        var rows = new[]
        {
            new OptimizationReportRow(new Dictionary<string, decimal> { ["RiskPercent"] = 1.5m }, 42m),
            new OptimizationReportRow(new Dictionary<string, decimal> { ["RiskPercent"] = 1.0m }, 99m), // higher score but NOT first -- report must not re-sort
        };
        var report = OptimizationReport.Build(rows);

        Assert.Equal(42m, report.Winner!.CompositeScore); // trusts caller's ranking, doesn't override with its own
    }

    [Fact]
    public void ToCsv_IncludesOneColumnPerParameter_PlusScore()
    {
        var rows = new[] { new OptimizationReportRow(new Dictionary<string, decimal> { ["RiskPercent"] = 1.5m, ["AtrMultiplier"] = 2m }, 42m) };
        var csv = OptimizationReport.Build(rows).ToCsv();

        Assert.True(csv.Contains("RiskPercent"));
        Assert.True(csv.Contains("AtrMultiplier"));
        Assert.True(csv.Contains("CompositeScore"));
    }

    [Fact]
    public void ToHtml_SummarizesWinner()
    {
        var rows = new[] { new OptimizationReportRow(new Dictionary<string, decimal> { ["RiskPercent"] = 1.5m }, 42m) };
        var html = OptimizationReport.Build(rows).ToHtml();
        Assert.True(html.Contains("42.00"));
    }

    [Fact]
    public void Build_EmptyRows_WinnerIsNull()
    {
        var report = OptimizationReport.Build(Array.Empty<OptimizationReportRow>());
        Assert.True(report.Winner is null);
    }
}

public class MonteCarloReportTests
{
    private static MonteCarloResult SampleResult() => new(
        P5EndingCapital: 800_000m, P25EndingCapital: 950_000m, MedianEndingCapital: 1_100_000m,
        P75EndingCapital: 1_300_000m, P95EndingCapital: 1_600_000m,
        ProbabilityDrawdownExceeds20Percent: 30m, ProbabilityDrawdownExceeds30Percent: 10m, ProbabilityDrawdownExceeds40Percent: 2m,
        ProbabilityOfRuin: 0.5m, IterationCount: 1000);

    [Fact]
    public void Build_IncludesBothMonteCarloAndGrowthScenarios()
    {
        var report = MonteCarloReport.Build(SampleResult(), 1_000_000m);
        Assert.Equal(1000, report.MonteCarlo.IterationCount);
        Assert.Equal(21, report.GrowthScenarios.Count);
    }

    [Fact]
    public void ToHtml_IncludesNotAForecastDisclaimer()
    {
        var html = MonteCarloReport.Build(SampleResult(), 1_000_000m).ToHtml();
        Assert.True(html.Contains("not forecasts"));
    }

    [Fact]
    public void ToCsv_ListsGrowthScenarioRows()
    {
        var csv = MonteCarloReport.Build(SampleResult(), 1_000_000m).ToCsv();
        Assert.Equal(22, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // header + 21 scenarios
    }
}
