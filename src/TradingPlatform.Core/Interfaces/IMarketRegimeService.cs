namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Determines deployable capital via the market regime engine (spec §3.1).</summary>
public interface IMarketRegimeService
{
    Task<MarketRegimeSnapshot> GetCurrentRegimeAsync(DateOnly asOfDate, CancellationToken cancellationToken = default);
}
