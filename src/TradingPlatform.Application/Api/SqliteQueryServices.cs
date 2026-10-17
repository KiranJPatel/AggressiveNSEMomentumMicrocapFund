using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Risk.Analytics;

namespace TradingPlatform.Application.Api;

/// <summary>
/// Real, repository-backed implementations of the eight GET /api/*
/// query-service seams (Tranche 17's ApiQueryServices.cs), replacing
/// PlaceholderQueryServices now that Tranche 18 has built the underlying
/// repositories. Honest caveat worth stating once here rather than on
/// every class: until `paper`/`live` are wired (still pending — see
/// docs/gap-log.md), nothing writes to Orders/Signals/Positions/RiskEvents,
/// so those endpoints will legitimately return empty results — the same
/// empty response PlaceholderQueryServices gave, but now because no data
/// exists yet rather than because the query layer is stubbed. `backtest`
/// and `scan` DO persist immediately, so /api/backtests and /api/scanner
/// return real data as soon as either command has been run once.
/// </summary>
public sealed class SqlitePortfolioQueryService : IPortfolioQueryService
{
    private readonly IPortfolioSnapshotRepository _snapshots;
    public SqlitePortfolioQueryService(IPortfolioSnapshotRepository snapshots) => _snapshots = snapshots;
    public Task<PortfolioSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => _snapshots.GetLatestAsync(cancellationToken);
}

public sealed class SqlitePositionQueryService : IPositionQueryService
{
    private readonly IPositionRepository _positions;
    public SqlitePositionQueryService(IPositionRepository positions) => _positions = positions;
    public Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken cancellationToken = default) => _positions.GetOpenAsync(cancellationToken);
}

public sealed class SqliteOrderQueryService : IOrderQueryService
{
    private readonly IOrderRepository _orders;
    public SqliteOrderQueryService(IOrderRepository orders) => _orders = orders;
    public Task<IReadOnlyList<Order>> GetRecentOrdersAsync(int limit, CancellationToken cancellationToken = default) => _orders.GetRecentAsync(limit, cancellationToken);
}

public sealed class SqliteSignalQueryService : ISignalQueryService
{
    private readonly ISignalRepository _signals;
    public SqliteSignalQueryService(ISignalRepository signals) => _signals = signals;
    public Task<IReadOnlyList<Signal>> GetRecentSignalsAsync(int limit, CancellationToken cancellationToken = default) => _signals.GetRecentAsync(limit, cancellationToken);
}

public sealed class SqliteScannerQueryService : IScannerQueryService
{
    private readonly IScannerResultRepository _results;
    public SqliteScannerQueryService(IScannerResultRepository results) => _results = results;
    public Task<IReadOnlyList<ScannerResult>> GetLatestResultsAsync(CancellationToken cancellationToken = default) => _results.GetLatestCycleAsync(cancellationToken);
}

public sealed class SqliteBacktestQueryService : IBacktestQueryService
{
    private readonly IBacktestRunRepository _runs;
    public SqliteBacktestQueryService(IBacktestRunRepository runs) => _runs = runs;
    public Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default) => _runs.GetRunsAsync(cancellationToken);
    public Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default) => _runs.GetRunAsync(id, cancellationToken);

    public async Task<BacktestRunDetailDto?> GetRunDetailAsync(long id, CancellationToken cancellationToken = default)
    {
        var run = await _runs.GetRunAsync(id, cancellationToken);
        if (run is null) return null;

        var trades = await _runs.GetTradesAsync(id, cancellationToken);
        var snapshots = await _runs.GetSnapshotsAsync(id, cancellationToken);
        var drawdownEvents = await _runs.GetDrawdownEventsAsync(id, cancellationToken);

        return new BacktestRunDetailDto(run, trades, snapshots, drawdownEvents, ComputeYearlyReturns(snapshots, trades));
    }

    /// <summary>
    /// Groups the daily-snapshot equity curve by calendar year: each
    /// year's return is (last Nav that year / last Nav before that year,
    /// or the run's own starting capital for the first year) - 1. Trade
    /// count per year is by ExitAt (the year the position closed), not
    /// EntryAt, matching how a P&amp;L statement attributes a trade to
    /// the year it was realized.
    /// </summary>
    private static IReadOnlyList<YearlyReturnDto> ComputeYearlyReturns(IReadOnlyList<PortfolioSnapshot> snapshots, IReadOnlyList<Trade> trades)
    {
        if (snapshots.Count == 0) return Array.Empty<YearlyReturnDto>();

        var byYear = snapshots.GroupBy(s => s.AsOfDate.Year).OrderBy(g => g.Key).ToList();
        var result = new List<YearlyReturnDto>(byYear.Count);
        decimal? previousYearEndNav = null;

        foreach (var year in byYear)
        {
            var ordered = year.OrderBy(s => s.AsOfDate).ToList();
            var yearStartNav = previousYearEndNav ?? ordered[0].Nav;
            var yearEndNav = ordered[^1].Nav;
            var returnPct = yearStartNav > 0 ? (yearEndNav - yearStartNav) / yearStartNav * 100m : 0m;
            var tradeCount = trades.Count(t => t.ExitAt.Year == year.Key);

            result.Add(new YearlyReturnDto(year.Key.ToString(), returnPct, tradeCount));
            previousYearEndNav = yearEndNav;
        }

        return result;
    }
}

public sealed class SqliteInstrumentQueryService : IInstrumentQueryService
{
    private readonly IInstrumentRepository _instruments;
    public SqliteInstrumentQueryService(IInstrumentRepository instruments) => _instruments = instruments;
    public Task<IReadOnlyList<Instrument>> GetActiveInstrumentsAsync(CancellationToken cancellationToken = default) => _instruments.GetActiveAsync(cancellationToken);
}

public sealed class SqliteChartQueryService : IChartQueryService
{
    private const int WarmupCalendarDays = 500;

    private readonly IInstrumentRepository _instruments;
    private readonly IHistoricalBarRepository _bars;
    private readonly IUniverseClassificationRepository _classifications;
    private readonly ISurveillanceRepository _surveillance;
    private readonly IndicatorOptions _indicatorOptions;
    private readonly VcpOptions _vcpOptions;
    private readonly RsOptions _rsOptions;
    private readonly BenchmarkOptions _benchmarkOptions;

    public SqliteChartQueryService(
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, IndicatorOptions indicatorOptions, VcpOptions vcpOptions, RsOptions rsOptions, BenchmarkOptions benchmarkOptions)
    {
        _instruments = instruments;
        _bars = bars;
        _classifications = classifications;
        _surveillance = surveillance;
        _indicatorOptions = indicatorOptions;
        _vcpOptions = vcpOptions;
        _rsOptions = rsOptions;
        _benchmarkOptions = benchmarkOptions;
    }

    public async Task<ChartDataDto?> GetChartDataAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var instrument = await _instruments.GetBySymbolAsync(symbol, cancellationToken);
        if (instrument is null) return null;

        var barsFrom = from.AddDays(-WarmupCalendarDays);
        var bars = await _bars.GetRangeAsync(symbol, barsFrom, to, Timeframe.Daily, cancellationToken);
        if (bars.Count == 0) return null;

        // Indicators computed once over the full (warm-up-included) series — same
        // "provably causal, index i never depends on bars after i" discipline
        // BacktestEngine relies on (Tranche 3/11) — then both bars and indicators
        // are trimmed together to the requested [from, to] window for the response.
        var indicators = TradingPlatform.Indicators.IndicatorEngine.ComputeSeries(bars, _indicatorOptions);
        var vcp = TradingPlatform.Strategy.Vcp.VcpDetector.Detect(bars, _vcpOptions);

        var fromIndex = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            if (DateOnly.FromDateTime(bars[i].Timestamp.Date) >= from) { fromIndex = i; break; }
            fromIndex = bars.Count; // every bar is before `from`
        }
        var trimmedBars = bars.Skip(fromIndex).ToList();
        var trimmedIndicators = indicators.Skip(fromIndex).ToList();

        decimal? rsScore = null;
        var broadBars = await _bars.GetRangeAsync(_benchmarkOptions.BroadIndexSymbol, barsFrom, to, Timeframe.Daily, cancellationToken);
        if (broadBars.Count > 0)
        {
            var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>> { [TradingPlatform.Strategy.RelativeStrength.BenchmarkNames.BroadIndex] = broadBars };
            var rsResult = TradingPlatform.Strategy.RelativeStrength.RelativeStrengthEngine.Calculate(bars, benchmarks, _rsOptions);
            rsScore = rsResult.BlendedScore;
        }

        var classification = await _classifications.GetClassificationAsync(symbol, to, cancellationToken);
        var surveillance = await _surveillance.GetStatusAsync(symbol, to, cancellationToken);

        return new ChartDataDto(symbol, trimmedBars, trimmedIndicators, vcp, rsScore, classification?.MarketCapBucket ?? MarketCapBucket.Unknown, surveillance);
    }
}

public sealed class SqliteRiskQueryService : IRiskQueryService
{
    private readonly IDrawdownEventRepository _drawdownEvents;
    private readonly IPortfolioSnapshotRepository _snapshots;
    private readonly IRiskEventRepository _riskEvents;
    private readonly RiskOptions _riskOptions;

    public SqliteRiskQueryService(IDrawdownEventRepository drawdownEvents, IPortfolioSnapshotRepository snapshots, IRiskEventRepository riskEvents, RiskOptions riskOptions)
    {
        _drawdownEvents = drawdownEvents;
        _snapshots = snapshots;
        _riskEvents = riskEvents;
        _riskOptions = riskOptions;
    }

    public async Task<RiskStatusDto> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        var latestSnapshot = await _snapshots.GetLatestAsync(cancellationToken);
        var latestDrawdownEvent = await _drawdownEvents.GetMostRecentAsync(cancellationToken);
        var allSnapshots = await _snapshots.GetRangeAsync(DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        var maxDrawdownEver = allSnapshots.Count > 0 ? allSnapshots.Max(s => s.DrawdownPercent) : 0m;

        // Resolved convention (Tranche 18): current kill-switch/loss-limit
        // status is reconstructed from the most recent matching RiskEvent
        // rather than a separate mutable "current state" table — an
        // Activated event with no later Cleared event means still active.
        // Nothing currently WRITES these events (paper/live aren't wired
        // yet), so this always resolves to "false"/"Normal" today; the
        // logic is real and ready for when it does.
        var killSwitchActive = await IsActiveAsync("KillSwitchActivated", "KillSwitchCleared", cancellationToken);
        var dailyBreached = await IsActiveAsync("DailyLossLimitBreached", "DailyLossLimitCleared", cancellationToken);
        var weeklyBreached = await IsActiveAsync("WeeklyLossLimitBreached", "WeeklyLossLimitCleared", cancellationToken);
        var monthlyBreached = await IsActiveAsync("MonthlyLossLimitBreached", "MonthlyLossLimitCleared", cancellationToken);

        RiskEvent? activeKillSwitchEvent = killSwitchActive ? await _riskEvents.GetMostRecentByTypeAsync("KillSwitchActivated", cancellationToken) : null;

        return new RiskStatusDto(
            CurrentDrawdownPercent: latestSnapshot?.DrawdownPercent ?? 0m,
            MaxDrawdownPercent: maxDrawdownEver,
            CurrentTierName: latestDrawdownEvent?.TierName ?? "Normal",
            TradingShutDown: latestDrawdownEvent?.TradingShutdown ?? false,
            PortfolioHeatPercent: latestSnapshot?.PortfolioHeatPercent ?? 0m,
            PortfolioHeatMaxPercent: _riskOptions.PortfolioHeatMaxPercent,
            DailyLossLimitBreached: dailyBreached,
            WeeklyLossLimitBreached: weeklyBreached,
            MonthlyLossLimitBreached: monthlyBreached,
            KillSwitchActive: killSwitchActive,
            // Source isn't tracked as a separate structured field on
            // RiskEvent today (only free-text Description) — left null
            // rather than guessing; KillSwitchReason carries the real text.
            KillSwitchSource: null,
            KillSwitchReason: activeKillSwitchEvent?.Description);
    }

    private async Task<bool> IsActiveAsync(string activatedEventType, string clearedEventType, CancellationToken cancellationToken)
    {
        var activated = await _riskEvents.GetMostRecentByTypeAsync(activatedEventType, cancellationToken);
        if (activated is null) return false;
        var cleared = await _riskEvents.GetMostRecentByTypeAsync(clearedEventType, cancellationToken);
        return cleared is null || cleared.Timestamp < activated.Timestamp;
    }
}

public sealed class SqlitePerformanceQueryService : IPerformanceQueryService
{
    private readonly ITradeRepository _trades;
    private readonly IPortfolioSnapshotRepository _snapshots;
    private readonly PortfolioOptions _portfolioOptions;
    private readonly IndicatorOptions _indicatorOptions;

    public SqlitePerformanceQueryService(ITradeRepository trades, IPortfolioSnapshotRepository snapshots, PortfolioOptions portfolioOptions, IndicatorOptions indicatorOptions)
    {
        _trades = trades;
        _snapshots = snapshots;
        _portfolioOptions = portfolioOptions;
        _indicatorOptions = indicatorOptions;
    }

    public async Task<PerformanceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var liveTrades = await _trades.GetLiveTradesAsync(cancellationToken);
        var analytics = TradeAnalyticsCalculator.Calculate(liveTrades);

        var latestSnapshot = await _snapshots.GetLatestAsync(cancellationToken);
        var currentCapital = latestSnapshot?.Nav ?? _portfolioOptions.InitialCapital;

        var history = await _snapshots.GetRangeAsync(DateOnly.MinValue, DateOnly.MaxValue, cancellationToken);
        decimal? cagr = null, sharpe = null, sortino = null, maxDrawdown = null;
        if (history.Count > 0)
        {
            var totalDays = history[^1].AsOfDate.DayNumber - history[0].AsOfDate.DayNumber;
            cagr = PerformanceMetricsCalculator.ComputeCagr(_portfolioOptions.InitialCapital, currentCapital, totalDays);
            (sharpe, sortino) = PerformanceMetricsCalculator.ComputeSharpeSortino(history.Select(s => s.Nav).ToList(), _indicatorOptions.TradingDaysPerYear);
            maxDrawdown = history.Max(s => s.DrawdownPercent);
        }

        return new PerformanceSummaryDto(
            CurrentCapital: currentCapital,
            CagrToDate: cagr,
            MaxDrawdownPercent: maxDrawdown,
            Sharpe: sharpe,
            Sortino: sortino,
            WinRatePercent: analytics.WinRatePercent,
            ProfitFactor: analytics.ProfitFactor,
            ExpectancyR: analytics.ExpectancyR,
            TradeCount: analytics.TradeCount);
    }
}

public sealed class SqliteMetricsQueryService : IMetricsQueryService
{
    private const int LookbackForTodayFilter = 500; // generous — enough recent rows to reliably cover "today" even on a busy day, without an unbounded table scan

    private readonly IPortfolioSnapshotRepository _snapshots;
    private readonly ISignalRepository _signals;
    private readonly IOrderRepository _orders;
    private readonly IPositionRepository _positions;

    public SqliteMetricsQueryService(IPortfolioSnapshotRepository snapshots, ISignalRepository signals, IOrderRepository orders, IPositionRepository positions)
    {
        _snapshots = snapshots;
        _signals = signals;
        _orders = orders;
        _positions = positions;
    }

    public async Task<SystemMetricsDto> GetCurrentMetricsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var today = now.Date;

        var latestSnapshot = await _snapshots.GetLatestAsync(cancellationToken);
        var openPositions = await _positions.GetOpenAsync(cancellationToken);

        var recentSignals = await _signals.GetRecentAsync(LookbackForTodayFilter, cancellationToken);
        var signalsToday = recentSignals.Count(s => s.GeneratedAt.Date == today);

        var recentOrders = await _orders.GetRecentAsync(LookbackForTodayFilter, cancellationToken);
        var ordersToday = recentOrders.Where(o => o.CreatedAt.Date == today).ToList();
        var submittedToday = ordersToday.Count(o => o.State is not OrderState.Created);
        var filledToday = ordersToday.Count(o => o.State is OrderState.Filled or OrderState.PartiallyFilled);
        var rejectedToday = ordersToday.Count(o => o.State is OrderState.Rejected or OrderState.Failed);

        return new SystemMetricsDto(
            CurrentPortfolioHeatPercent: latestSnapshot?.PortfolioHeatPercent,
            CurrentDrawdownPercent: latestSnapshot?.DrawdownPercent,
            CurrentRegime: latestSnapshot?.Regime,
            OpenPositionCount: openPositions.Count,
            SignalsGeneratedToday: signalsToday,
            OrdersSubmittedToday: submittedToday,
            OrdersFilledToday: filledToday,
            OrdersRejectedToday: rejectedToday,
            AsOf: now);
    }
}
