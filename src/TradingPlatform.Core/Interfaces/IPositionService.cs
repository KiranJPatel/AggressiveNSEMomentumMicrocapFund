namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Broker-facing position/holdings query service, used for reconciliation (spec §6.4).</summary>
public interface IPositionService
{
    Task<IReadOnlyList<Position>> GetBrokerPositionsAsync(CancellationToken cancellationToken = default);
}
