using Microsoft.Extensions.Logging;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;

namespace TradingPlatform.Application.Cli;

public sealed record IntradayCoverage(int SymbolsWithHourly, int SymbolsTotal, DateOnly? FirstHourlySession, DateOnly? LastHourlySession, int SymbolsWithQuarterHour);

/// <summary>Attaches cached 60m (and, for Refinement15m, 15m) bars to the backtest symbol contexts (spec §7.3). A no-op for NextOpen.</summary>
public static class IntradayContextLoader
{
    private const int BaselineWarmupDays = 14;   // earlier sessions feed the hourly-volume baseline

    public static async Task<(IReadOnlyList<BacktestSymbolContext> Symbols, IntradayCoverage? Coverage)> AttachAsync(
        IReadOnlyList<BacktestSymbolContext> symbols, IHistoricalBarRepository bars, SignalToOrderLag mode, DateOnly from, DateOnly to,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        if (mode == SignalToOrderLag.NextOpen) return (symbols, null);

        var attached = new List<BacktestSymbolContext>(symbols.Count);
        int withHourly = 0, withQuarter = 0;
        DateOnly? first = null, last = null;
        foreach (var s in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hourly = await bars.GetRangeAsync(s.Symbol, from.AddDays(-BaselineWarmupDays), to, Timeframe.H1, cancellationToken);
            var quarter = mode == SignalToOrderLag.Refinement15m
                ? await bars.GetRangeAsync(s.Symbol, from, to, Timeframe.M15, cancellationToken)
                : null;
            if (hourly.Count > 0)
            {
                withHourly++;
                var f = DateOnly.FromDateTime(hourly[0].Timestamp.Date); var l = DateOnly.FromDateTime(hourly[^1].Timestamp.Date);
                if (first is null || f < first) first = f;
                if (last is null || l > last) last = l;
            }
            if (quarter is { Count: > 0 }) withQuarter++;
            attached.Add(s.WithIntraday(hourly.Count > 0 ? hourly : null, quarter is { Count: > 0 } ? quarter : null));
        }

        logger.LogInformation("backtest: intraday refinement {Mode}: 60m bars for {With}/{Total} symbols ({First}..{Last}); 15m for {Quarter}.", mode, withHourly, symbols.Count, first, last, withQuarter);
        return (attached, new IntradayCoverage(withHourly, symbols.Count, first, last, withQuarter));
    }
}
