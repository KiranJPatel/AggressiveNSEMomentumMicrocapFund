using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Api;

/// <summary>
/// Implements spec Section 7.5's "thin REST API surface for future
/// dashboard consumption": GET /api/portfolio /positions /orders
/// /signals /scanner /risk /performance /backtests(/{id}). "No frontend
/// required now... thin, pure-consumer surface" - every handler here is
/// read-only (GET only, no mutation endpoints) and does nothing but call
/// through to a query-service interface and shape the response; all
/// actual business logic already lives in the engines built across every
/// prior tranche.
///
/// Handlers are public static methods (not inline route lambdas) taking
/// their query-service dependency as a plain parameter, specifically so
/// each is directly unit-testable by calling it with a fake service
/// implementation - no HTTP server, no WebApplicationFactory/TestHost
/// needed (neither is available offline in this sandbox; see
/// docs/gap-log.md), since a minimal-API handler is nothing more than a
/// method taking its dependencies as parameters. MapGet's role here is
/// pure route-to-handler wiring, deliberately with no logic of its own to
/// test separately.
/// </summary>
public static class ApiEndpoints
{
    public const int DefaultLimit = 50;

    public static void MapTradingPlatformApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portfolio", GetPortfolio);
        app.MapGet("/api/positions", GetPositions);
        app.MapGet("/api/orders", GetOrders);
        app.MapGet("/api/signals", GetSignals);
        app.MapGet("/api/scanner", GetScannerResults);
        app.MapGet("/api/risk", GetRisk);
        app.MapGet("/api/performance", GetPerformance);
        app.MapGet("/api/backtests", GetBacktestRuns);
        app.MapGet("/api/backtests/{id:long}", GetBacktestRun);
        app.MapGet("/api/backtests/{id:long}/detail", GetBacktestRunDetail);
        app.MapGet("/api/metrics", GetMetrics);
        app.MapGet("/api/instruments", GetInstruments);
        app.MapGet("/api/chart/{symbol}", GetChartData);
    }

    public static async Task<Results<Ok<PortfolioSnapshot>, NotFound>> GetPortfolio(
        IPortfolioQueryService service, CancellationToken cancellationToken = default)
    {
        var snapshot = await service.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null ? TypedResults.NotFound() : TypedResults.Ok(snapshot);
    }

    public static async Task<Ok<IReadOnlyList<Position>>> GetPositions(
        IPositionQueryService service, CancellationToken cancellationToken = default)
    {
        var positions = await service.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(positions);
    }

    public static async Task<Ok<IReadOnlyList<Order>>> GetOrders(
        IOrderQueryService service, int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        var orders = await service.GetRecentOrdersAsync(NormalizeLimit(limit), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(orders);
    }

    public static async Task<Ok<IReadOnlyList<Signal>>> GetSignals(
        ISignalQueryService service, int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        var signals = await service.GetRecentSignalsAsync(NormalizeLimit(limit), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(signals);
    }

    public static async Task<Ok<IReadOnlyList<ScannerResult>>> GetScannerResults(
        IScannerQueryService service, CancellationToken cancellationToken = default)
    {
        var results = await service.GetLatestResultsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(results);
    }

    public static async Task<Ok<RiskStatusDto>> GetRisk(
        IRiskQueryService service, CancellationToken cancellationToken = default)
    {
        var status = await service.GetCurrentStatusAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(status);
    }

    public static async Task<Ok<PerformanceSummaryDto>> GetPerformance(
        IPerformanceQueryService service, CancellationToken cancellationToken = default)
    {
        var summary = await service.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(summary);
    }

    public static async Task<Ok<IReadOnlyList<BacktestRun>>> GetBacktestRuns(
        IBacktestQueryService service, CancellationToken cancellationToken = default)
    {
        var runs = await service.GetRunsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(runs);
    }

    public static async Task<Results<Ok<BacktestRun>, NotFound>> GetBacktestRun(
        long id, IBacktestQueryService service, CancellationToken cancellationToken = default)
    {
        var run = await service.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run);
    }

    public static async Task<Ok<SystemMetricsDto>> GetMetrics(
        IMetricsQueryService service, CancellationToken cancellationToken = default)
    {
        var metrics = await service.GetCurrentMetricsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(metrics);
    }

    public static async Task<Results<Ok<BacktestRunDetailDto>, NotFound>> GetBacktestRunDetail(
        long id, IBacktestQueryService service, CancellationToken cancellationToken = default)
    {
        var detail = await service.GetRunDetailAsync(id, cancellationToken).ConfigureAwait(false);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    public static async Task<Ok<IReadOnlyList<Instrument>>> GetInstruments(
        IInstrumentQueryService service, CancellationToken cancellationToken = default)
    {
        var instruments = await service.GetActiveInstrumentsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(instruments);
    }

    /// <summary>?from/?to default to the last ~2 trading years when omitted — enough history for every overlay indicator (Sma200 needs the most) to be fully warmed up well before the visible window starts, without the caller having to know that.</summary>
    public static async Task<Results<Ok<ChartDataDto>, NotFound>> GetChartData(
        string symbol, IChartQueryService service, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var resolvedTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var resolvedFrom = from ?? resolvedTo.AddYears(-2);
        var data = await service.GetChartDataAsync(symbol, resolvedFrom, resolvedTo, cancellationToken).ConfigureAwait(false);
        return data is null ? TypedResults.NotFound() : TypedResults.Ok(data);
    }

    /// <summary>Clamps a caller-supplied ?limit= to a sane range - never trusts an unbounded or negative value straight through to a query service.</summary>
    private static int NormalizeLimit(int requested) => Math.Clamp(requested <= 0 ? DefaultLimit : requested, 1, 500);
}
