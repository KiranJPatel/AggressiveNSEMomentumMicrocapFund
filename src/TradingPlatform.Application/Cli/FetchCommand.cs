using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Manual smoke-test command for IHistoricalMarketDataProvider (Tranche 2).
/// This sandbox cannot reach query1.finance.yahoo.com (see docs/gap-log.md),
/// so the Yahoo integration has only ever been exercised against hand-built
/// fixture payloads in the test suite — this command is how a human, running
/// with real network access, verifies the actual live pipeline end-to-end:
///
///   dotnet run --project src/TradingPlatform.Application -- fetch --symbol RELIANCE --from 2026-01-01 --to 2026-01-31
/// </summary>
public static class FetchCommand
{
    public static async Task<int> RunAsync(string[] args, IHistoricalMarketDataProvider provider, ILogger logger)
    {
        var symbol = GetArg(args, "--symbol");
        var fromStr = GetArg(args, "--from");
        var toStr = GetArg(args, "--to");

        if (symbol is null || fromStr is null || toStr is null)
        {
            Console.Error.WriteLine("Usage: fetch --symbol <SYMBOL> --from <yyyy-MM-dd> --to <yyyy-MM-dd>");
            return 1;
        }

        if (!DateOnly.TryParse(fromStr, out var from) || !DateOnly.TryParse(toStr, out var to))
        {
            Console.Error.WriteLine("--from and --to must be valid dates in yyyy-MM-dd format.");
            return 1;
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var bars = await provider.GetBarsAsync(symbol, from, to, Timeframe.Daily);
            stopwatch.Stop();
            logger.LogInformation("fetch: {Symbol} [{From}..{To}] -> {BarCount} bar(s) in {ElapsedMs}ms (data-fetch latency).", symbol, from, to, bars.Count, stopwatch.ElapsedMilliseconds);

            Console.WriteLine($"Fetched {bars.Count} bar(s) for {symbol} [{from}..{to}] (cache + any newly downloaded ranges):");
            Console.WriteLine();
            Console.WriteLine($"{"Date",-12} {"Open",10} {"High",10} {"Low",10} {"Close",10} {"AdjClose",10} {"Volume",12}");
            foreach (var bar in bars.OrderBy(b => b.Timestamp))
            {
                Console.WriteLine($"{bar.Timestamp:yyyy-MM-dd} {bar.Open,10:F2} {bar.High,10:F2} {bar.Low,10:F2} {bar.Close,10:F2} {bar.AdjustedClose,10:F2} {bar.Volume,12}");
            }

            Console.WriteLine();
            Console.WriteLine("Any data-quality flags raised during this fetch were logged to the console above,");
            Console.WriteLine("the rolling log file, and the SystemEvents table (Category = 'DataQuality').");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "fetch command failed for {Symbol}", symbol);
            Console.Error.WriteLine($"Fetch failed: {ex.Message}");
            return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
