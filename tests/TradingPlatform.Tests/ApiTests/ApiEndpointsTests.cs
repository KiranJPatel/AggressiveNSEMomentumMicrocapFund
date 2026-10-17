using Microsoft.AspNetCore.Http.HttpResults;
using TradingPlatform.Api;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ApiTests;

public class ApiEndpointsTests
{
    private sealed class FakePortfolioQueryService : IPortfolioQueryService
    {
        public PortfolioSnapshot? Snapshot { get; set; }
        public Task<PortfolioSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }

    private sealed class FakePositionQueryService : IPositionQueryService
    {
        public IReadOnlyList<Position> Positions { get; set; } = Array.Empty<Position>();
        public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Positions);
    }

    private sealed class FakeOrderQueryService : IOrderQueryService
    {
        public int LastRequestedLimit;
        public IReadOnlyList<Order> Orders { get; set; } = Array.Empty<Order>();
        public Task<IReadOnlyList<Order>> GetRecentOrdersAsync(int limit, CancellationToken cancellationToken = default)
        {
            LastRequestedLimit = limit;
            return Task.FromResult(Orders);
        }
    }

    private sealed class FakeSignalQueryService : ISignalQueryService
    {
        public Task<IReadOnlyList<Signal>> GetRecentSignalsAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Signal>>(Array.Empty<Signal>());
    }

    private sealed class FakeScannerQueryService : IScannerQueryService
    {
        public IReadOnlyList<ScannerResult> Results { get; set; } = Array.Empty<ScannerResult>();
        public Task<IReadOnlyList<ScannerResult>> GetLatestResultsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Results);
    }

    private sealed class FakeRiskQueryService : IRiskQueryService
    {
        public RiskStatusDto Status { get; set; } = new(0m, 0m, "FullAggression", false, 0m, 0m, false, false, false, false, null, null);
        public Task<RiskStatusDto> GetCurrentStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
    }

    private sealed class FakePerformanceQueryService : IPerformanceQueryService
    {
        public PerformanceSummaryDto Summary { get; set; } = new(1_000_000m, null, null, null, null, null, null, null, 0);
        public Task<PerformanceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(Summary);
    }

    private sealed class FakeBacktestQueryService : IBacktestQueryService
    {
        public IReadOnlyList<BacktestRun> Runs { get; set; } = Array.Empty<BacktestRun>();
        public BacktestRun? SingleRun { get; set; }
        public BacktestRunDetailDto? Detail { get; set; }
        public Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Runs);
        public Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(SingleRun);
        public Task<BacktestRunDetailDto?> GetRunDetailAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Detail);
    }

    private sealed class FakeMetricsQueryService : IMetricsQueryService
    {
        public SystemMetricsDto Metrics { get; set; } = new(null, null, null, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow);
        public Task<SystemMetricsDto> GetCurrentMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Metrics);
    }

    private sealed class FakeInstrumentQueryService : IInstrumentQueryService
    {
        public IReadOnlyList<Instrument> Instruments { get; set; } = Array.Empty<Instrument>();
        public Task<IReadOnlyList<Instrument>> GetActiveInstrumentsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Instruments);
    }

    private sealed class FakeChartQueryService : IChartQueryService
    {
        public ChartDataDto? Data { get; set; }
        public Task<ChartDataDto?> GetChartDataAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) => Task.FromResult(Data);
    }

    private static PortfolioSnapshot Snapshot() => new()
    {
        AsOfDate = new DateOnly(2026, 1, 5), Nav = 1_000_000m, CashBalance = 500_000m, PeakNav = 1_000_000m,
        DrawdownPercent = 0m, PortfolioHeatPercent = 3m, OpenPositionCount = 2, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetPortfolio_WhenSnapshotExists_ReturnsOkWithSnapshot()
    {
        var service = new FakePortfolioQueryService { Snapshot = Snapshot() };
        var result = await ApiEndpoints.GetPortfolio(service);

        Assert.True(result.Result is Ok<PortfolioSnapshot>);
        var ok = (Ok<PortfolioSnapshot>)result.Result!;
        Assert.Equal(1_000_000m, ok.Value!.Nav);
    }

    [Fact]
    public async Task GetPortfolio_WhenNoSnapshot_ReturnsNotFound()
    {
        var service = new FakePortfolioQueryService { Snapshot = null };
        var result = await ApiEndpoints.GetPortfolio(service);

        Assert.True(result.Result is NotFound);
    }

    [Fact]
    public async Task GetPositions_ReturnsOkWithList()
    {
        var service = new FakePositionQueryService { Positions = new[] { new Position { Symbol = "ABC", IsOpen = true } } };
        var result = await ApiEndpoints.GetPositions(service);

        Assert.Equal(1, result.Value!.Count);
        Assert.Equal("ABC", result.Value[0].Symbol);
    }

    [Fact]
    public async Task GetOrders_ClampsNegativeLimit_ToDefault()
    {
        var service = new FakeOrderQueryService();
        await ApiEndpoints.GetOrders(service, limit: -5);
        Assert.Equal(ApiEndpoints.DefaultLimit, service.LastRequestedLimit);
    }

    [Fact]
    public async Task GetOrders_ClampsExcessiveLimit_To500()
    {
        var service = new FakeOrderQueryService();
        await ApiEndpoints.GetOrders(service, limit: 100_000);
        Assert.Equal(500, service.LastRequestedLimit);
    }

    [Fact]
    public async Task GetOrders_PassesThroughAValidLimit_Unchanged()
    {
        var service = new FakeOrderQueryService();
        await ApiEndpoints.GetOrders(service, limit: 25);
        Assert.Equal(25, service.LastRequestedLimit);
    }

    [Fact]
    public async Task GetScannerResults_ReturnsOkWithResults()
    {
        var service = new FakeScannerQueryService
        {
            Results = new[] { new ScannerResult { ScanTimestamp = DateTimeOffset.UtcNow, Rank = 1, Symbol = "ABC", Price = 100m, BreakoutStatus = "Breakout confirmed" } },
        };
        var result = await ApiEndpoints.GetScannerResults(service);
        Assert.Equal(1, result.Value!.Count);
    }

    [Fact]
    public async Task GetRisk_ReturnsOkWithStatus()
    {
        var service = new FakeRiskQueryService { Status = new RiskStatusDto(15m, 20m, "ReduceRisk20", false, 4m, 8m, false, false, false, false, null, null) };
        var result = await ApiEndpoints.GetRisk(service);
        Assert.Equal(15m, result.Value!.CurrentDrawdownPercent);
    }

    [Fact]
    public async Task GetPerformance_ReturnsOkWithSummary()
    {
        var service = new FakePerformanceQueryService();
        var result = await ApiEndpoints.GetPerformance(service);
        Assert.Equal(1_000_000m, result.Value!.CurrentCapital);
    }

    [Fact]
    public async Task GetBacktestRuns_ReturnsOkWithList()
    {
        var service = new FakeBacktestQueryService { Runs = new[] { MakeRun() } };
        var result = await ApiEndpoints.GetBacktestRuns(service);
        Assert.Equal(1, result.Value!.Count);
    }

    [Fact]
    public async Task GetBacktestRun_WhenFound_ReturnsOk()
    {
        var service = new FakeBacktestQueryService { SingleRun = MakeRun() };
        var result = await ApiEndpoints.GetBacktestRun(1, service);
        Assert.True(result.Result is Ok<BacktestRun>);
    }

    [Fact]
    public async Task GetBacktestRun_WhenMissing_ReturnsNotFound()
    {
        var service = new FakeBacktestQueryService { SingleRun = null };
        var result = await ApiEndpoints.GetBacktestRun(999, service);
        Assert.True(result.Result is NotFound);
    }

    [Fact]
    public async Task GetMetrics_ReturnsOkWithCurrentMetrics()
    {
        var service = new FakeMetricsQueryService
        {
            Metrics = new SystemMetricsDto(4.5m, 12m, RegimeClassification.Normal, 3, 2, 5, 4, 1, DateTimeOffset.UtcNow),
        };
        var result = await ApiEndpoints.GetMetrics(service);
        Assert.Equal(4.5m, result.Value!.CurrentPortfolioHeatPercent);
        Assert.Equal(3, result.Value!.OpenPositionCount);
        Assert.Equal(RegimeClassification.Normal, result.Value!.CurrentRegime);
    }

    [Fact]
    public async Task GetBacktestRunDetail_WhenFound_ReturnsOk()
    {
        var run = MakeRun();
        var detail = new BacktestRunDetailDto(run, Array.Empty<Trade>(), Array.Empty<PortfolioSnapshot>(), Array.Empty<DrawdownEvent>(), Array.Empty<YearlyReturnDto>());
        var service = new FakeBacktestQueryService { Detail = detail };
        var result = await ApiEndpoints.GetBacktestRunDetail(1, service);
        Assert.True(result.Result is Ok<BacktestRunDetailDto>);
    }

    [Fact]
    public async Task GetBacktestRunDetail_WhenMissing_ReturnsNotFound()
    {
        var service = new FakeBacktestQueryService { Detail = null };
        var result = await ApiEndpoints.GetBacktestRunDetail(999, service);
        Assert.True(result.Result is NotFound);
    }

    [Fact]
    public async Task GetInstruments_ReturnsOkWithActiveList()
    {
        var service = new FakeInstrumentQueryService
        {
            Instruments = new[] { new Instrument { Symbol = "RELIANCE", NseSymbol = "RELIANCE", YahooSymbol = "RELIANCE.NS", CompanyName = "Reliance Industries", Exchange = "NSE", Active = true } },
        };
        var result = await ApiEndpoints.GetInstruments(service);
        Assert.Single(result.Value!);
        Assert.Equal("RELIANCE", result.Value![0].Symbol);
    }

    [Fact]
    public async Task GetChartData_WhenFound_ReturnsOk()
    {
        var data = new ChartDataDto("RELIANCE", Array.Empty<HistoricalBar>(), Array.Empty<IndicatorSnapshot>(), null, 65m, MarketCapBucket.Large, null);
        var service = new FakeChartQueryService { Data = data };
        var result = await ApiEndpoints.GetChartData("RELIANCE", service);
        Assert.True(result.Result is Ok<ChartDataDto>);
    }

    [Fact]
    public async Task GetChartData_WhenSymbolUnknown_ReturnsNotFound()
    {
        var service = new FakeChartQueryService { Data = null };
        var result = await ApiEndpoints.GetChartData("NOSUCHSYMBOL", service);
        Assert.True(result.Result is NotFound);
    }

    private static BacktestRun MakeRun() => new()
    {
        StrategyVersion = "v1", ConfigurationHash = "c1", DataVersion = "d1",
        StartDate = new DateOnly(2020, 1, 1), EndDate = new DateOnly(2021, 1, 1),
        Status = Core.Enums.BacktestRunStatus.Completed, SurvivorshipBiased = true,
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
    };
}
