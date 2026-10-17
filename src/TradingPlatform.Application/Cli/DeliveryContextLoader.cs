using Microsoft.Extensions.Logging;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

/// <summary>Loads imported delivery % (spec §3.4) for the scan / backtest paths — and only when scoring or the breakout gate actually uses it, so a run without delivery pays no query cost.</summary>
public static class DeliveryContextLoader
{
    public static bool IsNeeded(StockScoringOptions scoring, StrategyOptions strategy) => scoring.UseDeliveryPercent || strategy.BreakoutMinDeliveryRatio is not null;

    /// <summary>Calendar days of history ahead of a date needed for the baseline average (sessions x 2 covers weekends and holidays).</summary>
    private static int WarmupDays(StockScoringOptions s) => Math.Max(1, s.DeliveryLookbackDays) * 2 + 14;

    public static async Task<IReadOnlyList<DeliveryRecord>?> LoadForScanAsync(
        IDeliveryDataRepository? repo, string symbol, DateOnly asOf, StockScoringOptions scoring, StrategyOptions strategy, CancellationToken ct)
    {
        if (repo is null || !IsNeeded(scoring, strategy)) return null;
        var records = await repo.GetRangeAsync(symbol, asOf.AddDays(-WarmupDays(scoring)), asOf, ct);
        return records.Count > 0 ? records : null;
    }

    public static async Task<IReadOnlyList<BacktestSymbolContext>> AttachToBacktestAsync(
        IReadOnlyList<BacktestSymbolContext> symbols, IDeliveryDataRepository? repo, StockScoringOptions scoring, StrategyOptions strategy,
        DateOnly from, DateOnly to, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
    {
        if (repo is null || !IsNeeded(scoring, strategy)) return symbols;
        var attached = new List<BacktestSymbolContext>(symbols.Count);
        var withData = 0;
        foreach (var s in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var records = await repo.GetRangeAsync(s.Symbol, from.AddDays(-WarmupDays(scoring)), to, ct);
            if (records.Count > 0) withData++;
            attached.Add(s.WithDelivery(records.Count > 0 ? records : null));
        }
        logger.LogInformation("backtest: delivery % available for {With}/{Total} symbols.", withData, symbols.Count);
        if (withData == 0)
            Console.Error.WriteLine("Warning: delivery % is enabled (scoring or breakout gate) but no delivery data is imported — " +
                (strategy.BreakoutMinDeliveryRatio is not null ? "the delivery gate will block EVERY entry (it fails closed). " : "scoring is unchanged. ") + "Use 'import-delivery' first.");
        return attached;
    }
}
