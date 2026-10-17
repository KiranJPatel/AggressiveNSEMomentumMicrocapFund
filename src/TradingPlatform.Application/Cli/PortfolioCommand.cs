using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Interfaces;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- portfolio` (spec §7.3) — prints the current
/// portfolio/positions/risk summary straight from local SQLite state (the
/// same repositories GET /api/portfolio, /positions and /risk use — see
/// SqliteQueryServices). Deliberately read-only: this never talks to the
/// broker (that's `reconcile`'s job, still pending — see docs/gap-log.md).
/// </summary>
public static class PortfolioCommand
{
    public static async Task<int> RunAsync(
        IPortfolioSnapshotRepository snapshots, IPositionRepository positions, IDrawdownEventRepository drawdownEvents,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        var openPositions = await positions.GetOpenAsync(cancellationToken);
        var lastDrawdownEvent = await drawdownEvents.GetMostRecentAsync(cancellationToken);

        Console.WriteLine("========================================================");
        Console.WriteLine(" PORTFOLIO SUMMARY");
        Console.WriteLine("========================================================");

        if (latest is null)
        {
            // Honest, not fabricated: no live/paper trading has ever run
            // and persisted a snapshot in this database — there is
            // genuinely no portfolio state to report yet (`paper`/`live`
            // are the commands that would create one; both remain
            // unimplemented — see docs/gap-log.md).
            Console.WriteLine("No portfolio snapshot on record yet.");
            Console.WriteLine("Run 'dotnet run -- paper' or 'dotnet run -- live' to begin building portfolio history.");
        }
        else
        {
            Console.WriteLine($"As of          : {latest.AsOfDate:yyyy-MM-dd}");
            Console.WriteLine($"NAV            : {latest.Nav:N2}");
            Console.WriteLine($"Cash           : {latest.CashBalance:N2}");
            Console.WriteLine($"Peak NAV       : {latest.PeakNav:N2}");
            Console.WriteLine($"Drawdown       : {latest.DrawdownPercent:F2}%");
            Console.WriteLine($"Portfolio Heat : {latest.PortfolioHeatPercent:F2}%");
            Console.WriteLine($"Regime         : {(latest.Regime is null ? "n/a" : latest.Regime.ToString())}");
        }

        Console.WriteLine();
        Console.WriteLine($"Open Positions : {openPositions.Count}");
        foreach (var p in openPositions.OrderBy(p => p.Symbol))
        {
            Console.WriteLine(
                $"  {p.Symbol,-12} Qty={p.Quantity,-8:N0} Avg={p.AverageEntryPrice,-10:N2} Stop={p.CurrentStop,-10:N2} Stage={p.HighestStageFilled}");
        }

        Console.WriteLine();
        if (lastDrawdownEvent is null)
        {
            Console.WriteLine("Drawdown tier  : Normal (no tier transitions on record)");
        }
        else
        {
            Console.WriteLine(
                $"Drawdown tier  : {lastDrawdownEvent.TierName} (as of {lastDrawdownEvent.Timestamp:yyyy-MM-dd}, risk reduction {lastDrawdownEvent.RiskReductionPercent:F0}%, shutdown={lastDrawdownEvent.TradingShutdown})");
        }

        Console.WriteLine("========================================================");

        logger.LogInformation("portfolio: printed summary. OpenPositions={Count} HasSnapshot={HasSnapshot}", openPositions.Count, latest is not null);
        return 0;
    }
}
