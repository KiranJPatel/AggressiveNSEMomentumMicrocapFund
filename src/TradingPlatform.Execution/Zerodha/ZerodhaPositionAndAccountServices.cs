using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>Implements IPositionService (spec §6.4) — fetches broker net positions for reconciliation against local SQLite state.</summary>
public sealed class ZerodhaPositionService : IPositionService
{
    private readonly KiteApiGateway _gateway;

    public ZerodhaPositionService(KiteApiGateway gateway) => _gateway = gateway;

    public async Task<IReadOnlyList<Position>> GetBrokerPositionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gateway.SendGeneralRequestAsync(HttpMethod.Get, "/portfolio/positions", null, cancellationToken).ConfigureAwait(false);
        return KiteResponseParser.ParsePositions(response.StatusCode, response.Body);
    }
}

/// <summary>Implements IAccountService (spec §6.2) — fetches available cash for pre-trade sufficient-cash validation and reconciliation.</summary>
public sealed class ZerodhaAccountService : IAccountService
{
    private readonly KiteApiGateway _gateway;

    public ZerodhaAccountService(KiteApiGateway gateway) => _gateway = gateway;

    public async Task<decimal> GetAvailableCashAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gateway.SendGeneralRequestAsync(HttpMethod.Get, "/user/margins/equity", null, cancellationToken).ConfigureAwait(false);
        return KiteResponseParser.ParseAvailableCash(response.StatusCode, response.Body);
    }
}
