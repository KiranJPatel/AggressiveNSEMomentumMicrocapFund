namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

/// <summary>
/// Local SQLite cache of OHLCV bars (spec §5.2). Distinct from
/// IHistoricalMarketDataProvider: that interface fetches from an external
/// source (Yahoo Finance); this one is the local read/write cache a caching
/// decorator sits in front of.
/// </summary>
public interface IHistoricalBarRepository
{
    Task<IReadOnlyList<HistoricalBar>> GetRangeAsync(string symbol, DateOnly from, DateOnly to, Timeframe timeframe, CancellationToken cancellationToken = default);

    /// <summary>Upserts on the (Symbol, Timestamp, Timeframe) unique key — safe to call repeatedly with overlapping ranges.</summary>
    Task UpsertRangeAsync(IReadOnlyList<HistoricalBar> bars, CancellationToken cancellationToken = default);

    /// <summary>First/last cached date and bar count per symbol, in one query — drives incremental downloads and `universe stats`.</summary>
    Task<IReadOnlyList<BarCoverage>> GetCoverageAsync(Timeframe timeframe, CancellationToken cancellationToken = default);
}
