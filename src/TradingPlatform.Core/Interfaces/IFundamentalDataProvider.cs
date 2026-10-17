namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// Fundamental data source (spec §3.2). Fields Yahoo doesn't reliably provide
/// historically must come back with DataAvailability.Unavailable, never a
/// silently-defaulted positive value.
/// </summary>
public interface IFundamentalDataProvider
{
    Task<FundamentalSnapshot> GetFundamentalsAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default);
}
