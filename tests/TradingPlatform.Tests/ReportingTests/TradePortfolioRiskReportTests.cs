using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ReportingTests;

public class TradeReportTests
{
    [Fact]
    public void Build_ToCsv_OneRowPerTrade()
    {
        var trades = new[] { SelectionFixture.MakeTrade("ABC", 100m), SelectionFixture.MakeTrade("XYZ", -50m) };
        var report = TradeReport.Build(trades);

        var csv = report.ToCsv();

        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // header + 2 rows
        Assert.True(csv.Contains("ABC"));
        Assert.True(csv.Contains("XYZ"));
    }

    [Fact]
    public void ToJson_ContainsRealizedPnl()
    {
        var trades = new[] { SelectionFixture.MakeTrade("ABC", 100m) };
        var json = TradeReport.Build(trades).ToJson();
        Assert.True(json.Contains("realizedPnl", StringComparison.OrdinalIgnoreCase) || json.Contains("RealizedPnl"));
    }

    [Fact]
    public void ToHtml_IncludesTradeCountInTitle()
    {
        var trades = new[] { SelectionFixture.MakeTrade("ABC", 100m), SelectionFixture.MakeTrade("XYZ", -50m) };
        var html = TradeReport.Build(trades).ToHtml();
        Assert.True(html.Contains("2 trades"));
    }
}

public class PortfolioReportTests
{
    private static PortfolioSnapshot Snapshot(decimal nav) => new()
    {
        AsOfDate = new DateOnly(2020, 1, 1), Nav = nav, CashBalance = nav, PeakNav = nav,
        DrawdownPercent = 0m, PortfolioHeatPercent = 3m, OpenPositionCount = 1, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ToCsv_OneRowPerSnapshot()
    {
        var report = PortfolioReport.Build(new[] { Snapshot(1_000_000m), Snapshot(1_050_000m) });
        var csv = report.ToCsv();
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ToCsv_MissingRegime_RendersAsNotAvailable()
    {
        var report = PortfolioReport.Build(new[] { Snapshot(1_000_000m) });
        Assert.True(report.ToCsv().Contains("n/a"));
    }
}

public class RiskReportTests
{
    private static DrawdownEvent Event(decimal drawdownPercent, string tier, bool shutdown) => new()
    {
        DrawdownPercent = drawdownPercent, TierName = tier, RiskReductionPercent = 50m, TradingShutdown = shutdown, Timestamp = DateTimeOffset.UtcNow,
    };

    private static PortfolioSnapshot Snapshot(decimal heat, decimal drawdown) => new()
    {
        AsOfDate = new DateOnly(2020, 1, 1), Nav = 1_000_000m, CashBalance = 1_000_000m, PeakNav = 1_000_000m,
        DrawdownPercent = drawdown, PortfolioHeatPercent = heat, OpenPositionCount = 1, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Build_ComputesMaxDrawdownObserved_FromSnapshots()
    {
        var report = RiskReport.Build(Array.Empty<DrawdownEvent>(), new[] { Snapshot(2m, 5m), Snapshot(2m, 12m), Snapshot(2m, 8m) });
        Assert.Equal(12m, report.MaxDrawdownPercentObserved);
    }

    [Fact]
    public void Build_UsesLatestEvent_ForCurrentTierAndShutdownStatus()
    {
        var events = new[] { Event(10m, "NormalReduced", false), Event(42m, "TradingShutdown", true) };
        var report = RiskReport.Build(events, Array.Empty<PortfolioSnapshot>());

        Assert.Equal("TradingShutdown", report.CurrentTierName);
        Assert.True(report.CurrentlyShutDown);
    }

    [Fact]
    public void Build_NoEvents_NotCurrentlyShutDown()
    {
        var report = RiskReport.Build(Array.Empty<DrawdownEvent>(), Array.Empty<PortfolioSnapshot>());
        Assert.False(report.CurrentlyShutDown);
    }

    [Fact]
    public void ToHtml_IncludesEventRows()
    {
        var events = new[] { Event(10m, "NormalReduced", false) };
        var html = RiskReport.Build(events, Array.Empty<PortfolioSnapshot>()).ToHtml();
        Assert.True(html.Contains("NormalReduced"));
    }
}
