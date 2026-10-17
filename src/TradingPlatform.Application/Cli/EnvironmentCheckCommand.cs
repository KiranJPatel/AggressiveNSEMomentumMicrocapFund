using Microsoft.Extensions.Logging;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Implements the environment-disclosure requirement (spec §0.2) as a live,
/// re-runnable check rather than a one-time note. Run `dotnet run -- env` in
/// whatever environment is actually hosting this tool (which may well have
/// full internet access even though the environment this was built in did
/// not) to see current reachability of every external dependency the spec
/// calls for, and which hand-rolled substitutions are currently in effect.
/// </summary>
public static class EnvironmentCheckCommand
{
    private static readonly (string Name, string Url, string UsedFor)[] ExternalDependencies =
    {
        ("NuGet (api.nuget.org)", "https://api.nuget.org/v3/index.json", "Restoring third-party packages (Serilog, Polly, Microsoft.Data.Sqlite, Skender.Stock.Indicators, xunit, Zerodha SDK, ...)"),
        ("Yahoo Finance (query1.finance.yahoo.com)", "https://query1.finance.yahoo.com", "Historical OHLCV data (IHistoricalMarketDataProvider)"),
        ("Zerodha Kite Connect (api.kite.trade)", "https://api.kite.trade", "Live/paper broker integration (IZerodhaClient)"),
    };

    public static async Task<int> RunAsync(ILogger logger)
    {
        Console.WriteLine("========================================================");
        Console.WriteLine(" ENVIRONMENT DISCLOSURE (spec §0.2)");
        Console.WriteLine("========================================================");
        Console.WriteLine($".NET SDK        : {Environment.Version} (target net8.0)");
        Console.WriteLine($"Repo root       : {Infrastructure.RepoPaths.RepoRoot}");
        Console.WriteLine();
        Console.WriteLine("External dependency reachability (live check, this run):");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var anyUnreachable = false;

        foreach (var dep in ExternalDependencies)
        {
            string status;
            try
            {
                var response = await http.GetAsync(dep.Url);
                if (response.Headers.TryGetValues("x-deny-reason", out var denyReasons))
                {
                    status = $"BLOCKED by network egress policy (HTTP {(int)response.StatusCode}, reason: {string.Join(",", denyReasons)})";
                    anyUnreachable = true;
                }
                else
                {
                    status = $"reachable (HTTP {(int)response.StatusCode})";
                }
            }
            catch (Exception ex)
            {
                status = $"UNREACHABLE ({ex.GetType().Name}: {ex.Message})";
                anyUnreachable = true;
            }

            Console.WriteLine($"  - {dep.Name,-42} {status}");
            Console.WriteLine($"      used for: {dep.UsedFor}");
            logger.LogInformation("Environment check: {Dependency} -> {Status}", dep.Name, status);
        }

        Console.WriteLine();
        Console.WriteLine("Hand-rolled substitutions currently in effect (see docs/gap-log.md):");
        Console.WriteLine("  - Logging       : hand-rolled console + rolling-file ILoggerProvider (Serilog unavailable)");
        Console.WriteLine("  - SQLite access : hand-rolled P/Invoke wrapper over libsqlite3 (present locally),");
        Console.WriteLine("                    not Microsoft.Data.Sqlite — wired since Tranche 1");
        Console.WriteLine("  - Unit testing  : hand-rolled attribute + reflection test runner (xunit/NUnit/MSTest unavailable)");
        Console.WriteLine();

        if (anyUnreachable)
        {
            Console.WriteLine("NOTE: one or more dependencies were unreachable from this environment.");
            Console.WriteLine("This does not necessarily mean they are unreachable for you — re-run this");
            Console.WriteLine("command wherever the tool is actually deployed to check that environment.");
        }

        return 0;
    }
}
