namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// AMFI/SEBI point-in-time market-cap-bucket source (spec §2.2). Returns the
/// snapshot effective on asOfDate — never today's bucket for a historical date.
/// </summary>
public interface IMarketCapClassificationProvider
{
    Task<UniverseClassificationSnapshot?> GetClassificationAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default);
}
