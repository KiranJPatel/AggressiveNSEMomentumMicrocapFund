using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Api;

/// <summary>
/// Temporary, honestly-labeled stand-in implementations of the 8 API
/// query-service interfaces (Tranche 17). Every method returns an empty
/// result (or null for a single-item lookup) rather than fabricated data.
/// SUPERSEDED as of Tranche 18: `serve` now wires SqliteQueryServices
/// (real repository-backed implementations) instead of this class — see
/// ServeCommand. Kept in the codebase (unused by any command) as a
/// reference for the "honest empty result, never fabricated data"
/// contract every query-service implementation must follow, and as a
/// ready-made zero-dependency fallback if a future `serve --demo`-style
/// mode ever wants one deliberately.
/// </summary>
public sealed class PlaceholderQueryServices :
    IPortfolioQueryService, IPositionQueryService, IOrderQueryService, ISignalQueryService,
    IScannerQueryService, IRiskQueryService, IPerformanceQueryService, IBacktestQueryService
{
    public Task<PortfolioSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PortfolioSnapshot?>(null);

    public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());

    public Task<IReadOnlyList<Order>> GetRecentOrdersAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());

    public Task<IReadOnlyList<Signal>> GetRecentSignalsAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Signal>>(Array.Empty<Signal>());

    public Task<IReadOnlyList<ScannerResult>> GetLatestResultsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScannerResult>>(Array.Empty<ScannerResult>());

    public Task<RiskStatusDto> GetCurrentStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RiskStatusDto(0m, 0m, "FullAggression", false, 0m, 0m, false, false, false, false, null, null));

    public Task<PerformanceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PerformanceSummaryDto(0m, null, null, null, null, null, null, null, 0));

    public Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BacktestRun>>(Array.Empty<BacktestRun>());

    public Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult<BacktestRun?>(null);

    public Task<BacktestRunDetailDto?> GetRunDetailAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult<BacktestRunDetailDto?>(null);
}
