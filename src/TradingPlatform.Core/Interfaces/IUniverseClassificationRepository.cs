namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// Read/write access to point-in-time market-cap-bucket/sector classification
/// (spec §2.2). Extends the read-only provider seam so strategy code can
/// depend on just IMarketCapClassificationProvider while ingestion code
/// (manual/CSV import) depends on this.
/// </summary>
public interface IUniverseClassificationRepository : IMarketCapClassificationProvider
{
    Task UpsertAsync(UniverseClassificationSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>All classification snapshots for the symbol, newest EffectiveFrom first.</summary>
    Task<IReadOnlyList<UniverseClassificationSnapshot>> GetHistoryAsync(string symbol, CancellationToken cancellationToken = default);
}
