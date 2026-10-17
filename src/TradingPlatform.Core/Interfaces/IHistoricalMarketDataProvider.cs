namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

/// <summary>
/// Provider-agnostic historical OHLCV source (spec §1.2, §5.2).
/// YahooFinanceHistoricalDataProvider implements this today; swappable later.
/// </summary>
public interface IHistoricalMarketDataProvider
{
    Task<IReadOnlyList<HistoricalBar>> GetBarsAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        Timeframe timeframe,
        CancellationToken cancellationToken = default);
}
