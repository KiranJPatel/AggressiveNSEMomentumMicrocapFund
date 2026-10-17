namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// Sector taxonomy source (spec §2.2) — manual/CSV/SQLite ingestion initially.
/// Must return null/Unknown explicitly rather than guessing, so a diversification
/// check never silently passes on a fabricated sector.
/// </summary>
public interface ISectorClassificationProvider
{
    Task<string?> GetSectorAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default);
}
