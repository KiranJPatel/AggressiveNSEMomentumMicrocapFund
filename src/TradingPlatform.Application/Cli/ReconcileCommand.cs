using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Reconciliation;
using TradingPlatform.Execution.Zerodha;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- reconcile` (spec §6.4, §6.5, §7.3) — compares local
/// SQLite positions/cash against the broker's, using ReconciliationEngine
/// (Tranche 12) as a pure comparison. In Paper/DryRun mode there is no
/// external broker to compare against by definition, so this prints local
/// state and says so plainly rather than fabricating a "clean" result
/// against nothing. In Live mode it attempts a real Kite API call —
/// wrapped defensively, since this sandbox has no network path to
/// kite.zerodha.com to verify that call ever succeeds (see
/// AdapterVerifiedAgainstLiveDocs in ZerodhaOptions).
/// </summary>
public static class ReconcileCommand
{
    public static async Task<int> RunAsync(
        IPositionRepository positions, IPortfolioSnapshotRepository snapshots, IRiskEventRepository riskEvents,
        LiveTradingOptions liveTradingOptions, ZerodhaOptions zerodhaOptions, PortfolioOptions portfolioOptions,
        IServiceProvider provider, ILogger logger, CancellationToken cancellationToken = default)
    {
        var localPositions = await positions.GetOpenAsync(cancellationToken);
        var localCash = (await snapshots.GetLatestAsync(cancellationToken))?.CashBalance ?? portfolioOptions.InitialCapital;

        Console.WriteLine("========================================================");
        Console.WriteLine(" RECONCILIATION");
        Console.WriteLine("========================================================");
        Console.WriteLine($"Mode: {liveTradingOptions.Mode}");
        Console.WriteLine($"Local open positions: {localPositions.Count}, local cash: {localCash:N2}");

        if (liveTradingOptions.Mode != TradingMode.Live)
        {
            Console.WriteLine();
            Console.WriteLine($"No external broker in {liveTradingOptions.Mode} mode — nothing to reconcile against.");
            PrintLocalPositions(localPositions);
            Console.WriteLine("========================================================");
            return 0;
        }

        var apiKey = Environment.GetEnvironmentVariable(zerodhaOptions.ApiKeyEnvVar);
        var accessToken = Environment.GetEnvironmentVariable(zerodhaOptions.AccessTokenEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(accessToken))
        {
            Console.WriteLine();
            Console.WriteLine($"Mode=Live but {zerodhaOptions.ApiKeyEnvVar}/{zerodhaOptions.AccessTokenEnvVar} are not both set — cannot reach the broker.");
            PrintLocalPositions(localPositions);
            Console.WriteLine("========================================================");
            logger.LogWarning("reconcile: Live mode but broker credentials are not configured.");
            return 1;
        }

        try
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var transport = new KiteHttpTransport(httpClientFactory, zerodhaOptions.RequestTimeoutSeconds);
            var gateway = new KiteApiGateway(transport, zerodhaOptions, apiKey, accessToken);
            var brokerPositionService = new ZerodhaPositionService(gateway);
            var brokerAccountService = new ZerodhaAccountService(gateway);

            var brokerPositions = await brokerPositionService.GetBrokerPositionsAsync(cancellationToken);
            var brokerCash = await brokerAccountService.GetAvailableCashAsync(cancellationToken);

            var result = ReconciliationEngine.Reconcile(localPositions, brokerPositions, localCash, brokerCash, liveTradingOptions.CashReconciliationToleranceRupees);

            Console.WriteLine();
            if (result.IsClean)
            {
                Console.WriteLine("CLEAN — local state matches the broker exactly.");
            }
            else
            {
                Console.WriteLine($"DIRTY — {result.Mismatches.Count} mismatch(es). Manual intervention required (spec §6.4: never auto-guess a fix).");
                foreach (var m in result.Mismatches)
                    Console.WriteLine($"  [{m.Symbol}] {m.Field}: local={m.LocalValue} broker={m.BrokerValue}");

                await riskEvents.InsertAsync(new RiskEvent
                {
                    EventType = "ReconciliationMismatch",
                    Description = $"{result.Mismatches.Count} mismatch(es) detected: " + string.Join("; ", result.Mismatches.Select(m => $"{m.Symbol}/{m.Field}")),
                    Timestamp = DateTimeOffset.UtcNow,
                    CorrelationId = Guid.NewGuid().ToString(),
                }, cancellationToken);
            }

            Console.WriteLine("========================================================");
            logger.LogInformation("reconcile: {Status}, {Count} mismatch(es).", result.IsClean ? "clean" : "dirty", result.Mismatches.Count);
            return result.IsClean ? 0 : 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "reconcile: broker call failed.");
            Console.WriteLine();
            Console.WriteLine($"Broker reconciliation failed: {ex.Message}");
            PrintLocalPositions(localPositions);
            Console.WriteLine("========================================================");
            return 1;
        }
    }

    private static void PrintLocalPositions(IReadOnlyList<Position> positions)
    {
        foreach (var p in positions.OrderBy(p => p.Symbol))
            Console.WriteLine($"  {p.Symbol,-12} Qty={p.Quantity,-8:N0} Avg={p.AverageEntryPrice:N2}");
    }
}
