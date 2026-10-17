namespace TradingPlatform.Core.Interfaces;

/// <summary>Broker-facing funds/margin query service (spec §6.2).</summary>
public interface IAccountService
{
    Task<decimal> GetAvailableCashAsync(CancellationToken cancellationToken = default);
}
