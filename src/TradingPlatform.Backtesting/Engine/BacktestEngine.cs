using TradingPlatform.Backtesting.Portfolio;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators;
using TradingPlatform.Risk;
using TradingPlatform.Risk.Analytics;
using TradingPlatform.Risk.Correlation;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Risk.Sizing;
using TradingPlatform.Risk.Trailing;
using TradingPlatform.Strategy;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Strategy.RelativeStrength;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Strategy.Scoring;
using TradingPlatform.Strategy.Vcp;

namespace TradingPlatform.Backtesting.Engine;

/// <summary>
/// The full backtest orchestration (spec §5.1): the ONE place every prior
/// tranche's pure engine gets wired together into a day-by-day historical
/// replay. Every individual piece (indicators, VCP, RS, regime, scoring,
/// StrategyStepEngine, position sizing, exits, trailing stops, drawdown,
/// loss limits) was already built and unit-tested pure/standalone in
/// Tranches 3-10 — this class's own job is purely the SEQUENCING and DATA
/// PLUMBING between them, which is exactly the "portfolio simulator" spec
/// §5.1 describes.
///
/// Per-day sequencing (deliberately in this order):
///   1. Mark today's prices, compute today's regime snapshot.
///   2. EXITS on existing positions, against TODAY's bar: StopLoss
///      (mechanism A) is checked and filled INTRADAY (spec §4.3's stop
///      behavior is explicitly NOT subject to the close-confirmation
///      convention); MomentumFailure/FundamentalFailure/RegimeFailure
///      (B/C/D) are signal-based like entries and only QUEUE an exit for
///      next session's open (spec §3.8's NextOpen convention) — this
///      timing distinction wasn't resolved anywhere before this tranche
///      and is a documented resolved ambiguity. Positions that neither
///      stop out nor get a B/C/D signal have their trailing stop
///      recomputed for tomorrow.
///   3. FILL yesterday's queued orders (entries AND B/C/D exits) against
///      TODAY's open — the NextOpen lag resolving one day later.
///   4. NAV / drawdown / loss-limit / portfolio-risk-gate, computed from
///      TODAY's close — governs TOMORROW's sizing, the same "compute at
///      close, act at next open" discipline used everywhere else.
///   5. NEW SIGNALS for tomorrow: VCP/RS/scoring/StrategyStepEngine per
///      symbol, gated by MaxPositions and PortfolioRiskGate.BlocksNewEntries,
///      sized by PositionSizingCalculator using
///      MIN(RegimeClassifier's DeployableCapitalPercent, PortfolioRiskGate's)
///      — the exact wiring Tranche 10's gap-log flagged as still pending.
///
/// Known, documented performance characteristic (not correctness): VCP/RS/
/// trailing-stop/momentum inputs are re-sliced "up to today" every day
/// (O(n) per day, O(n^2) per symbol overall by default) because these
/// algorithms treat "the last bar in the slice" as "today" and must be
/// evaluated fresh — indicators are the one exception, computed ONCE per
/// symbol up front, since they are provably causal (Tranche 3) and index
/// i's value never depends on bars after i. Fine at the bar counts this
/// tranche's tests use. Tranche 18 adds an OPT-IN fix
/// (BacktestOptions.BoundedLookbackWindowBars — see its XML doc) that caps
/// every such slice to a bounded recent window instead of the full
/// history-to-date, turning the per-symbol cost into O(n * window); it
/// defaults to null (unbounded, i.e. this file's exact pre-Tranche-18
/// behavior) specifically so the 600+ existing tests are provably
/// unaffected by its introduction — enabling it is a deliberate operator
/// choice for production-scale, multi-year runs, not a silent default
/// change.
/// </summary>
public static class BacktestEngine
{
    /// <param name="cancellationToken">Checked once per simulated trading day, so Ctrl+C interrupts a long run within one day's work instead of after the whole run. A cancelled run throws OperationCanceledException and returns no partial result.</param>
    public static BacktestRunResult Run(BacktestRunRequest request, BacktestEngineOptions options, CancellationToken cancellationToken = default)
    {
        if (request.StartDate > request.EndDate) throw new ArgumentException("StartDate must not be after EndDate.");
        if (request.Symbols.Count == 0) throw new ArgumentException("At least one symbol is required.");

        var symbolStates = request.Symbols.ToDictionary(s => s.Symbol, s => BuildSymbolRuntimeState(s, options));
        var sectorBySymbol = request.Symbols.ToDictionary(s => s.Symbol, s => s.Sector);
        var bucketBySymbol = request.Symbols.ToDictionary(s => s.Symbol, s => s.MarketCapBucket);

        var broadDates = request.BroadIndexBars.Select(b => DateOnly.FromDateTime(b.Timestamp.Date)).ToList();
        var midcapDates = request.MidcapIndexBars?.Select(b => DateOnly.FromDateTime(b.Timestamp.Date)).ToList();

        var tradingDates = broadDates.Where(d => d >= request.StartDate && d <= request.EndDate).Distinct().OrderBy(d => d).ToList();
        if (tradingDates.Count == 0)
            throw new ArgumentException("No trading dates found in [StartDate, EndDate] against BroadIndexBars — the broad index defines the trading calendar.");

        var portfolioState = BacktestPortfolioState.Initial(options.Portfolio.InitialCapital);
        DrawdownState? drawdownState = null;
        LossLimitState? lossLimitState = null;

        var dailySnapshots = new List<PortfolioSnapshot>();
        var drawdownEvents = new List<DrawdownEvent>();

        var pendingEntryOrders = new Dictionary<string, PendingEntryOrder>();
        var refinementTally = options.Backtest.SignalToOrderLag == SignalToOrderLag.NextOpen ? null : new Execution.RefinementTally();
        var runCounters = new RunCounters();
        var pendingExitOrders = new Dictionary<string, PendingExitOrder>();

        var startedAt = DateTimeOffset.UtcNow;

        foreach (var date in tradingDates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var todayBarIndex = new Dictionary<string, int>();
            var todayPrices = new Dictionary<string, decimal>();
            foreach (var (symbol, rs) in symbolStates)
            {
                if (rs.BarIndexByDate.TryGetValue(date, out var idx))
                {
                    todayBarIndex[symbol] = idx;
                    todayPrices[symbol] = rs.Context.Bars[idx].Close;
                }
            }

            var broadSlice = SliceUpToOrBefore(request.BroadIndexBars, broadDates, date, options.Backtest.BoundedLookbackWindowBars) ?? Array.Empty<HistoricalBar>();
            var midcapSlice = request.MidcapIndexBars is null || midcapDates is null ? null : SliceUpToOrBefore(request.MidcapIndexBars, midcapDates, date, options.Backtest.BoundedLookbackWindowBars);

            // Spec §3.1 universe-derived inputs, point-in-time: only bars on/before `date`, and only trades closed BEFORE `date`.
            var breadth = UniverseBreadthCalculator.Calculate(BuildBreadthObservations(symbolStates, date), options.MarketRegime.BreadthMinimumSampleSize);
            var breakoutSuccess = BreakoutSuccessRateCalculator.Calculate(portfolioState.ClosedTrades, date, options.MarketRegime);

            var regimeSnapshot = MarketRegimeEngine.Calculate(
                new MarketRegimeInputs
                {
                    BroadIndexBars = broadSlice, MidcapIndexBars = midcapSlice,
                    PercentUniverseAbove50Dma = breadth.PercentAbove50Dma, PercentUniverseAbove200Dma = breadth.PercentAbove200Dma,
                    AdvanceDeclineRatio = breadth.AdvanceDeclineRatio, BreakoutSuccessRatePercent = breakoutSuccess,
                },
                options.MarketRegime, options.Rs, date);

            var sectorStrengthCache = new Dictionary<string, decimal>();

            ProcessExits(date, portfolioState, symbolStates, todayBarIndex, regimeSnapshot, broadSlice, midcapSlice, sectorStrengthCache, pendingExitOrders, options);

            FillPendingExitOrders(portfolioState, symbolStates, todayBarIndex, pendingExitOrders, options);
            FillPendingEntryOrders(request, portfolioState, symbolStates, todayBarIndex, pendingEntryOrders, options, refinementTally, runCounters);

            var nav = PortfolioExposureCalculator.ComputeNav(portfolioState, todayPrices);
            var (newDrawdownState, ddEvent) = DrawdownEngine.Evaluate(drawdownState, date, nav, options.Risk);
            drawdownState = newDrawdownState;
            if (ddEvent is not null) drawdownEvents.Add(ddEvent);
            lossLimitState = PeriodicLossLimitEngine.Evaluate(lossLimitState, date, nav, options.Risk);
            var riskGate = PortfolioRiskGate.Evaluate(drawdownState, lossLimitState, options.Risk);

            var heatValue = PortfolioExposureCalculator.ComputePortfolioHeatValue(portfolioState, todayPrices);
            dailySnapshots.Add(new PortfolioSnapshot
            {
                AsOfDate = date,
                Nav = nav,
                CashBalance = portfolioState.Cash,
                PeakNav = drawdownState.PeakNav,
                DrawdownPercent = drawdownState.DrawdownPercent,
                PortfolioHeatPercent = nav > 0 ? heatValue / nav * 100m : 0m,
                OpenPositionCount = portfolioState.OpenPositions.Count,
                Regime = regimeSnapshot.Classification,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            GenerateSignals(request, portfolioState, symbolStates, todayBarIndex, regimeSnapshot, riskGate, broadSlice, midcapSlice, sectorStrengthCache,
                sectorBySymbol, bucketBySymbol, todayPrices, nav, pendingEntryOrders, options);
        }

        var run = BuildBacktestRun(request, portfolioState, dailySnapshots, drawdownState, startedAt, options);

        return new BacktestRunResult
        {
            Run = run,
            Trades = portfolioState.ClosedTrades,
            TradeLegs = portfolioState.ClosedTradeLegs,
            DailySnapshots = dailySnapshots,
            DrawdownEvents = drawdownEvents,
            Refinement = refinementTally?.ToStats(options.Backtest.SignalToOrderLag),
            OrphanedAddOnOrdersCancelled = runCounters.OrphanedAddOnsCancelled,
            EntryExecution = runCounters.GapChecked > 0
                ? new EntryExecutionStats(runCounters.GapChecked, runCounters.FillVsPlannedPercentSum / runCounters.GapChecked, runCounters.GapResized, runCounters.GapCancelled, options.Risk.ResizeEntryForGapRisk)
                : null,
        };
    }

    /// <summary>Mean hourly-bar volume over the last <paramref name="sessions"/> sessions BEFORE <paramref name="date"/> that have hourly data; null when there is none (the volume condition is then not evaluated).</summary>
    private static decimal? HourlyVolumeBaseline(SymbolRuntimeState rs, DateOnly date, int sessions)
    {
        var prior = rs.HourlyByDate.Where(kv => kv.Key < date).OrderByDescending(kv => kv.Key).Take(Math.Max(1, sessions)).SelectMany(kv => kv.Value).ToList();
        return prior.Count == 0 ? null : (decimal)prior.Average(b => (double)b.Volume);
    }

    private static List<BreadthObservation> BuildBreadthObservations(IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates, DateOnly date)
    {
        var list = new List<BreadthObservation>(symbolStates.Count);
        foreach (var rs in symbolStates.Values)
        {
            if (!rs.BarIndexByDate.TryGetValue(date, out var idx)) continue;
            var bars = rs.Context.Bars;
            var ind = rs.Indicators[idx];
            list.Add(new BreadthObservation(bars[idx].Close, idx > 0 ? bars[idx - 1].Close : null, ind.Sma50, ind.Sma200));
        }
        return list;
    }

    // ----------------------------------------------------------------
    // Step 2: exits
    // ----------------------------------------------------------------

    private static void ProcessExits(
        DateOnly date, BacktestPortfolioState portfolioState, IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates,
        IReadOnlyDictionary<string, int> todayBarIndex, MarketRegimeSnapshot regimeSnapshot,
        IReadOnlyList<HistoricalBar> broadSlice, IReadOnlyList<HistoricalBar>? midcapSlice, Dictionary<string, decimal> sectorStrengthCache,
        Dictionary<string, PendingExitOrder> pendingExitOrders, BacktestEngineOptions options)
    {
        foreach (var symbol in portfolioState.OpenPositions.Keys.ToList())
        {
            if (!todayBarIndex.TryGetValue(symbol, out var idx)) continue; // halted today — position persists untouched

            var rs = symbolStates[symbol];
            var bar = rs.Context.Bars[idx];
            var indicators = rs.Indicators[idx];
            var lot = portfolioState.OpenPositions[symbol];

            BacktestPortfolioSimulator.UpdateExcursion(lot, bar.High, bar.Low);

            var stockRs = ComputeStockRs(rs, broadSlice, midcapSlice, date, idx, options.Rs, options.Backtest.BoundedLookbackWindowBars);
            var fundamentalsToday = rs.Context.FundamentalsByDate?.GetValueOrDefault(date);
            var positionForEval = ToPosition(lot, symbol);

            var exitEval = Strategy.Exit.ExitEngine.Evaluate(positionForEval, bar, indicators, stockRs.BlendedScore, regimeSnapshot.Classification, fundamentalsToday, options.Exit);

            if (exitEval.ShouldExit && exitEval.PrimaryReason == ExitReason.StopLoss)
            {
                var order = new Order { Symbol = symbol, Side = OrderSide.Sell, Type = OrderType.StopLoss, Quantity = lot.Quantity, StopPrice = lot.CurrentStop, CorrelationId = Guid.NewGuid().ToString() };
                var adtv = ComputeAdtv(rs.Context.Bars, idx, options.Backtest.AdtvLookbackDays);
                var fill = Execution.BacktestFillSimulator.Simulate(order, bar, adtv, rs.Context.MarketCapBucket, options.Backtest, options.Risk);
                if (fill.Filled)
                    BacktestPortfolioSimulator.ApplySellFill(portfolioState, symbol, fill.FilledQuantity, fill.ExecutionPrice!.Value, fill.TransactionCosts!, bar.Timestamp, ExitReason.StopLoss);
                // Not filled (e.g. zero-volume bar): position stays open, re-evaluated tomorrow.
            }
            else if (exitEval.ShouldExit)
            {
                pendingExitOrders[symbol] = new PendingExitOrder(exitEval.PrimaryReason!.Value);
            }
            else
            {
                var barsUpToNow = BoundedSlice(rs.Context.Bars, idx, options.Backtest.BoundedLookbackWindowBars);
                var trailing = TrailingStopCalculator.Calculate(positionForEval, barsUpToNow, indicators, options.Strategy, options.Exit);
                lot.CurrentStop = trailing.NewStop;
            }
        }
    }

    // ----------------------------------------------------------------
    // Step 3: fill yesterday's queued orders against today's open
    // ----------------------------------------------------------------

    private static void FillPendingExitOrders(
        BacktestPortfolioState portfolioState, IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates,
        IReadOnlyDictionary<string, int> todayBarIndex, Dictionary<string, PendingExitOrder> pendingExitOrders, BacktestEngineOptions options)
    {
        foreach (var symbol in pendingExitOrders.Keys.ToList())
        {
            if (!portfolioState.OpenPositions.ContainsKey(symbol)) { pendingExitOrders.Remove(symbol); continue; } // already closed via an intraday stop today
            if (!todayBarIndex.TryGetValue(symbol, out var idx)) continue; // still halted — leave queued

            var rs = symbolStates[symbol];
            var bar = rs.Context.Bars[idx];
            var lot = portfolioState.OpenPositions[symbol];
            var pending = pendingExitOrders[symbol];

            var order = new Order { Symbol = symbol, Side = OrderSide.Sell, Type = OrderType.Market, Quantity = lot.Quantity, CorrelationId = Guid.NewGuid().ToString() };
            var adtv = ComputeAdtv(rs.Context.Bars, idx, options.Backtest.AdtvLookbackDays);
            var fill = Execution.BacktestFillSimulator.Simulate(order, bar, adtv, rs.Context.MarketCapBucket, options.Backtest, options.Risk);
            if (fill.Filled)
                BacktestPortfolioSimulator.ApplySellFill(portfolioState, symbol, fill.FilledQuantity, fill.ExecutionPrice!.Value, fill.TransactionCosts!, bar.Timestamp, pending.Reason);

            pendingExitOrders.Remove(symbol);
        }
    }

    private static void FillPendingEntryOrders(
        BacktestRunRequest request, BacktestPortfolioState portfolioState, IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates,
        IReadOnlyDictionary<string, int> todayBarIndex, Dictionary<string, PendingEntryOrder> pendingEntryOrders, BacktestEngineOptions options,
        Execution.RefinementTally? tally = null, RunCounters? counters = null)
    {
        foreach (var symbol in pendingEntryOrders.Keys.ToList())
        {
            if (!todayBarIndex.TryGetValue(symbol, out var idx)) continue; // still halted — leave queued

            var pending = pendingEntryOrders[symbol];
            var rs = symbolStates[symbol];
            var bar = rs.Context.Bars[idx];

            // A pyramid add-on (Stage 2/3) only makes sense while its position is open. Exits run BEFORE fills, so a position stopped out
            // (or otherwise exited) on the fill day leaves its queued add-on orphaned: the add-on carries no planned stop/risk, and trying to
            // fill it would open a position it cannot describe (this used to abort the whole backtest with InvalidOperationException).
            // Cancel it, and count it so the report can say so.
            if (pending.Stage != PyramidStage.Stage1Initial && !portfolioState.OpenPositions.ContainsKey(symbol))
            {
                if (counters is not null) counters.OrphanedAddOnsCancelled++;
                pendingEntryOrders.Remove(symbol);
                continue;
            }

            var executionBar = bar;
            if (tally is not null && pending.Stage == PyramidStage.Stage1Initial && pending.RefinementLevel is { } level)
            {
                var date = DateOnly.FromDateTime(bar.Timestamp.Date);
                tally.Attempted++;
                rs.HourlyByDate.TryGetValue(date, out var hourly);
                rs.QuarterByDate.TryGetValue(date, out var quarter);
                var refined = Execution.IntradayEntryRefiner.Refine(
                    options.Backtest.SignalToOrderLag, level, hourly, quarter, HourlyVolumeBaseline(rs, date, options.Backtest.RefinementVolumeBaselineSessions),
                    options.Backtest.RefinementConfirmBufferPercent, options.Backtest.RefinementMinTriggerRelativeVolume);

                switch (refined.Outcome)
                {
                    case Execution.RefinementOutcome.Executed:
                        // Same day's range and volume (the liquidity cap is a share of the DAY's volume), only the reference price moves.
                        executionBar = new HistoricalBar
                        {
                            Symbol = bar.Symbol, Timestamp = bar.Timestamp, Open = refined.ExecutionPrice!.Value, High = Math.Max(bar.High, refined.ExecutionPrice.Value),
                            Low = Math.Min(bar.Low, refined.ExecutionPrice.Value), Close = bar.Close, AdjustedClose = bar.AdjustedClose, Volume = bar.Volume,
                            Source = bar.Source, Timeframe = bar.Timeframe, CreatedAt = bar.CreatedAt,
                        };
                        tally.RecordExecution(refined.ExecutionPrice.Value, bar.Open);
                        break;
                    case Execution.RefinementOutcome.NoConfirmation:
                        tally.NoConfirmation++; pendingEntryOrders.Remove(symbol); continue;       // the breakout did not hold: no order
                    case Execution.RefinementOutcome.NoLaterBarToExecute:
                        tally.NoLaterBar++; pendingEntryOrders.Remove(symbol); continue;
                    case Execution.RefinementOutcome.NoIntradayData:
                        if (options.Backtest.RefinementSkipWhenNoIntradayData) { tally.SkippedNoData++; pendingEntryOrders.Remove(symbol); continue; }
                        tally.Fallback++;   // documented fallback: plain daily-open fill, counted so the report shows the refined share
                        break;
                }
            }

            // Gap-risk check (GapRiskAdjuster): sizing assumed an entry at the pivot, but the order executes at the open, which is often above it.
            // Scale the entry down so rupee risk stays at the planned budget (never up), or cancel it if the open is at/through the stop.
            var orderQuantity = pending.Quantity;
            var totalTarget = pending.PlannedTotalTargetQuantity;
            if (pending.Stage == PyramidStage.Stage1Initial && pending.PlannedInitialStop is { } plannedStop
                && pending.RefinementLevel is { } plannedEntry && pending.PlannedTotalTargetQuantity is { } plannedTotal && plannedEntry > 0m)
            {
                var gap = GapRiskAdjuster.Adjust(pending.Quantity, plannedTotal, plannedEntry, plannedStop, executionBar.Open);
                if (counters is not null) { counters.GapChecked++; counters.FillVsPlannedPercentSum += (executionBar.Open / plannedEntry - 1m) * 100m; }
                if (options.Risk.ResizeEntryForGapRisk)
                {
                    if (gap.Cancelled) { if (counters is not null) counters.GapCancelled++; pendingEntryOrders.Remove(symbol); continue; }
                    if (gap.Resized) { if (counters is not null) counters.GapResized++; orderQuantity = gap.Stage1Quantity; totalTarget = gap.TotalTargetQuantity; }
                }
            }

            var order = new Order { Symbol = symbol, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = orderQuantity, CorrelationId = Guid.NewGuid().ToString() };
            var adtv = ComputeAdtv(rs.Context.Bars, idx, options.Backtest.AdtvLookbackDays);
            var fill = Execution.BacktestFillSimulator.Simulate(order, executionBar, adtv, rs.Context.MarketCapBucket, options.Backtest, options.Risk);
            if (fill.Filled)
            {
                BacktestPortfolioSimulator.ApplyBuyFill(portfolioState, symbol, pending.Stage, fill.FilledQuantity, fill.ExecutionPrice!.Value, fill.TransactionCosts!, bar.Timestamp,
                    newPositionInitialStop: pending.PlannedInitialStop,
                    // Guard ON: risk is recorded from the ACTUAL fill (fill - stop), not the planned pivot entry, so R-multiples, MFE/MAE and the R-based
                    // trailing/pyramid triggers are measured against the risk the position really carries. Guard OFF: the legacy planned values, exactly as
                    // before. (Add-on orders carry no planned values and never open a position, so these are unused for them.)
                    newPositionRiskPerShare: options.Risk.ResizeEntryForGapRisk && pending.PlannedInitialStop is { } fillStop ? Math.Max(fill.ExecutionPrice!.Value - fillStop, 0.0001m) : pending.PlannedRiskPerShare,
                    newPositionInitialRiskAmount: options.Risk.ResizeEntryForGapRisk && pending.PlannedInitialStop is { } fillStop2 && totalTarget is { } tt ? Math.Max(fill.ExecutionPrice!.Value - fillStop2, 0.0001m) * tt : pending.PlannedInitialRiskAmount,
                    newPositionTotalTargetQuantity: totalTarget,
                    newPositionRegimeScoreAtEntry: pending.RegimeScoreAtEntry,
                    newPositionStockScoreAtEntry: pending.StockScoreAtEntry,
                    newPositionRiskAtEntryPercent: pending.RiskAtEntryPercent,
                    newPositionStrategyVersion: request.StrategyVersion,
                    newPositionConfigurationVersion: request.ConfigurationHash);
            }

            pendingEntryOrders.Remove(symbol);
        }
    }

    // ----------------------------------------------------------------
    // Step 5: generate tomorrow's signals
    // ----------------------------------------------------------------

    private static void GenerateSignals(
        BacktestRunRequest request, BacktestPortfolioState portfolioState, IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates,
        IReadOnlyDictionary<string, int> todayBarIndex, MarketRegimeSnapshot regimeSnapshot, PortfolioRiskGateResult riskGate,
        IReadOnlyList<HistoricalBar> broadSlice, IReadOnlyList<HistoricalBar>? midcapSlice, Dictionary<string, decimal> sectorStrengthCache,
        IReadOnlyDictionary<string, string?> sectorBySymbol, IReadOnlyDictionary<string, MarketCapBucket> bucketBySymbol,
        IReadOnlyDictionary<string, decimal> todayPrices, decimal nav, Dictionary<string, PendingEntryOrder> pendingEntryOrders, BacktestEngineOptions options)
    {
        foreach (var (symbol, rs) in symbolStates)
        {
            if (!todayBarIndex.TryGetValue(symbol, out var idx)) continue;
            if (pendingEntryOrders.ContainsKey(symbol)) continue;

            var bar = rs.Context.Bars[idx];
            var barsSlice = BoundedSlice(rs.Context.Bars, idx, options.Backtest.BoundedLookbackWindowBars);
            var indicatorsSlice = BoundedSlice(rs.Indicators, idx, options.Backtest.BoundedLookbackWindowBars);

            var vcp = VcpDetector.Detect(barsSlice, options.Vcp);
            var stockRs = ComputeStockRs(rs, broadSlice, midcapSlice, DateOnly.FromDateTime(bar.Timestamp.Date), idx, options.Rs, options.Backtest.BoundedLookbackWindowBars);

            portfolioState.OpenPositions.TryGetValue(symbol, out var lot);
            StagedPositionState? existingPositionState = lot is null ? null : new StagedPositionState
            {
                Symbol = symbol,
                HighestStageFilled = lot.HighestStageFilled,
                CurrentStop = lot.CurrentStop,
                Stage1EntryPrice = lot.Stage1EntryPrice,
                Stage1EntryTimestamp = lot.Stage1EntryTimestamp,
                Stage2EntryPrice = lot.Stage2EntryPrice,
                Stage2EntryTimestamp = lot.Stage2EntryTimestamp,
            };

            var stepInputs = new StrategyStepInputs
            {
                Symbol = symbol,
                Bars = barsSlice,
                IndicatorSnapshots = indicatorsSlice,
                VcpPattern = vcp,
                RelativeStrengthScore = stockRs.BlendedScore,
                Regime = regimeSnapshot.Classification,
                ExistingPosition = existingPositionState,
                TotalTargetQuantity = lot?.TotalTargetQuantity,
                Delivery = rs.DeliveryByDate.GetValueOrDefault(DateOnly.FromDateTime(barsSlice[^1].Timestamp.Date)),
            };

            var stepResult = StrategyStepEngine.Evaluate(stepInputs, options.Strategy, options.Rs);

            if (stepResult.Decision is StrategyStepDecision.NoSignal or StrategyStepDecision.HoldExisting) continue;
            if (riskGate.BlocksNewEntries) continue; // halts ALL new capital deployment, fresh entry or staged add alike

            if (stepResult.Decision == StrategyStepDecision.EnterStage1)
            {
                if (portfolioState.OpenPositions.Count >= options.Portfolio.MaxPositions) continue;

                var sectorStrength = ComputeSectorStrengthScore(rs.Context, broadSlice, DateOnly.FromDateTime(bar.Timestamp.Date), options.Rs, sectorStrengthCache, options.Backtest.BoundedLookbackWindowBars);
                var fundamentalsToday = rs.Context.FundamentalsByDate?.GetValueOrDefault(DateOnly.FromDateTime(bar.Timestamp.Date));
                var institutionalToday = rs.Context.InstitutionalActivityByDate?.GetValueOrDefault(DateOnly.FromDateTime(bar.Timestamp.Date));

                var stockScore = StockScoringEngine.Calculate(new StockScoringInputs
                {
                    Symbol = symbol,
                    AsOfDate = DateOnly.FromDateTime(bar.Timestamp.Date),
                    Bars = barsSlice,
                    IndicatorSnapshots = indicatorsSlice,
                    VcpPattern = vcp,
                    RelativeStrengthScore = stockRs.BlendedScore,
                    SectorStrengthScore = sectorStrength,
                    CurrentFundamentals = fundamentalsToday,
                    PriorFundamentals = null,
                    InstitutionalActivity = institutionalToday,
                    Delivery = rs.DeliveryByDate.GetValueOrDefault(DateOnly.FromDateTime(bar.Timestamp.Date)),
                }, options.Strategy, options.StockScoring);

                var entryPrice = vcp?.PivotPrice ?? bar.Close;
                var plannedStop = InitialStopCalculator.Calculate(rs.Context.Bars, idx, entryPrice, options.Risk);
                if (plannedStop >= entryPrice) continue; // RiskPerShare would be <= 0 — PositionSizingCalculator would reject anyway; skip queuing entirely

                var adtv = ComputeAdtv(rs.Context.Bars, idx, options.Backtest.AdtvLookbackDays);
                var deployablePercent = Math.Min(regimeSnapshot.DeployableCapitalPercent, riskGate.DeployableCapitalPercent);

                var sizingInputs = new PositionSizingInputs
                {
                    Symbol = symbol,
                    EntryPrice = entryPrice,
                    StopPrice = plannedStop,
                    Nav = nav,
                    CashBalance = portfolioState.Cash,
                    AverageDailyTradedValue = adtv,
                    StockScoreTotalScore = stockScore.TotalScore,
                    MarketCapBucket = rs.Context.MarketCapBucket,
                    CurrentSectorExposureValue = PortfolioExposureCalculator.ComputeSectorExposureValue(portfolioState, todayPrices, sectorBySymbol, rs.Context.Sector),
                    CurrentMicrocapExposureValue = PortfolioExposureCalculator.ComputeMicrocapExposureValue(portfolioState, todayPrices, bucketBySymbol),
                    CurrentPortfolioHeatValue = PortfolioExposureCalculator.ComputePortfolioHeatValue(portfolioState, todayPrices),
                    CorrelatedClusterExposureValue = ComputeCorrelatedClusterExposure(symbol, barsSlice, portfolioState, todayPrices, symbolStates, options.Risk),
                    DeployableCapitalPercent = deployablePercent,
                    TechnicalScore = stockScore.TechnicalScore,
                    FundamentalScore = stockScore.FundamentalScore,
                    HasCriticalSurveillanceFlag = rs.Context.HasCriticalSurveillanceFlag,
                    HasSevereDataQualityIssue = rs.Context.HasSevereDataQualityIssue,
                };

                var sizing = PositionSizingCalculator.Calculate(sizingInputs, options.Risk, options.Portfolio);
                if (sizing.Rejected || sizing.FinalQty <= 0) continue;

                var (stage1Qty, _, _) = StagedEntryCalculator.Split(sizing.FinalQty, options.Strategy);
                if (stage1Qty <= 0) continue;

                pendingEntryOrders[symbol] = new PendingEntryOrder(
                    PyramidStage.Stage1Initial, stage1Qty,
                    plannedStop, entryPrice - plannedStop, sizing.FinalQty,
                    regimeSnapshot.RegimeScore, stockScore.TotalScore, options.Risk.RiskPercentPerTrade, RefinementLevel: entryPrice);
            }
            else // AddStage2 / AddStage3
            {
                var addQty = stepResult.SuggestedQuantity ?? 0m;
                if (addQty <= 0) continue;

                var stage = stepResult.Decision == StrategyStepDecision.AddStage2 ? PyramidStage.Stage2Add : PyramidStage.Stage3Add;
                // Add quantities are predetermined by the original Stage 1 sizing split (spec §3.7) — not independently re-sized here.
                pendingEntryOrders[symbol] = new PendingEntryOrder(stage, addQty, null, null, null, regimeSnapshot.RegimeScore, 0m, 0m);
            }
        }
    }

    // ----------------------------------------------------------------
    // Shared helpers
    // ----------------------------------------------------------------

    private static SymbolRuntimeState BuildSymbolRuntimeState(BacktestSymbolContext context, BacktestEngineOptions options)
    {
        var indicatorOptions = options.Indicator;
        var indicators = IndicatorEngine.ComputeSeries(context.Bars, indicatorOptions);
        var barIndexByDate = new Dictionary<DateOnly, int>();
        for (var i = 0; i < context.Bars.Count; i++)
            barIndexByDate[DateOnly.FromDateTime(context.Bars[i].Timestamp.Date)] = i;

        var sectorBenchmarkDates = context.SectorBenchmarkBars?.Select(b => DateOnly.FromDateTime(b.Timestamp.Date)).ToList();

        return new SymbolRuntimeState
        {
            Context = context,
            Indicators = indicators,
            BarIndexByDate = barIndexByDate,
            SectorBenchmarkDates = sectorBenchmarkDates,
            HourlyByDate = GroupBySession(context.HourlyBars),
            QuarterByDate = GroupBySession(context.QuarterHourBars),
            // Only computed when delivery is actually used, so a run without it pays nothing.
            DeliveryByDate = context.DeliveryRecords is { Count: > 0 } deliveryRecords && (options.StockScoring.UseDeliveryPercent || options.Strategy.BreakoutMinDeliveryRatio is not null)
                ? DeliveryAnalyzer.EvaluateSeries(deliveryRecords, options.StockScoring)
                : new Dictionary<DateOnly, DeliverySnapshot>(),
        };
    }

    /// <summary>Run-level counters for events the report should disclose.</summary>
    private sealed class RunCounters
    {
        public int OrphanedAddOnsCancelled, GapChecked, GapResized, GapCancelled;
        public decimal FillVsPlannedPercentSum;
    }

    private static Dictionary<DateOnly, List<HistoricalBar>> GroupBySession(IReadOnlyList<HistoricalBar>? bars) =>
        bars is null ? new() : bars.OrderBy(b => b.Timestamp).GroupBy(b => DateOnly.FromDateTime(b.Timestamp.Date)).ToDictionary(g => g.Key, g => g.ToList());

    private static MultiBenchmarkRelativeStrengthResult ComputeStockRs(
        SymbolRuntimeState rs, IReadOnlyList<HistoricalBar> broadSlice, IReadOnlyList<HistoricalBar>? midcapSlice, DateOnly date, int idx, RsOptions rsOptions, int? maxLookback = null)
    {
        var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>> { [BenchmarkNames.BroadIndex] = broadSlice };
        if (midcapSlice is { Count: > 0 }) benchmarks[BenchmarkNames.Midcap] = midcapSlice;

        if (rs.Context.SectorBenchmarkBars is { Count: > 0 } sectorBars && rs.SectorBenchmarkDates is not null)
        {
            var sectorSlice = SliceUpToOrBefore(sectorBars, rs.SectorBenchmarkDates, date, maxLookback);
            if (sectorSlice is { Count: > 0 }) benchmarks[BenchmarkNames.Sector] = sectorSlice;
        }

        var stockBarsSlice = BoundedSlice(rs.Context.Bars, idx, maxLookback);
        return RelativeStrengthEngine.Calculate(stockBarsSlice, benchmarks, rsOptions);
    }

    /// <summary>Sector-index-vs-broad-index RS — identical for every stock in the same sector on a given day (see StockScoringInputs' XML doc), so computed once per sector per day via the caller-supplied cache.</summary>
    private static decimal? ComputeSectorStrengthScore(BacktestSymbolContext context, IReadOnlyList<HistoricalBar> broadSlice, DateOnly date, RsOptions rsOptions, Dictionary<string, decimal> cache, int? maxLookback = null)
    {
        if (context.Sector is not { } sector || context.SectorBenchmarkBars is not { Count: > 0 } sectorBars) return null;
        if (cache.TryGetValue(sector, out var cached)) return cached;

        var sectorDates = sectorBars.Select(b => DateOnly.FromDateTime(b.Timestamp.Date)).ToList();
        var sectorSlice = SliceUpToOrBefore(sectorBars, sectorDates, date, maxLookback);
        if (sectorSlice is not { Count: > 0 }) return null;

        var (sectorCloses, broadCloses) = PriceSeriesAligner.AlignByDate(sectorSlice, broadSlice);
        if (sectorCloses.Count == 0) return null;

        var score = RelativeStrengthCalculator.Calculate(sectorCloses, broadCloses, rsOptions).CompositeScore;
        cache[sector] = score;
        return score;
    }

    private static decimal ComputeCorrelatedClusterExposure(
        string candidateSymbol, IReadOnlyList<HistoricalBar> candidateBarsSlice, BacktestPortfolioState portfolioState,
        IReadOnlyDictionary<string, decimal> todayPrices, IReadOnlyDictionary<string, SymbolRuntimeState> symbolStates, RiskOptions riskOptions)
    {
        var existing = new List<CorrelatedPosition>();
        foreach (var (symbol, lot) in portfolioState.OpenPositions)
        {
            if (symbol == candidateSymbol) continue;
            if (!symbolStates.TryGetValue(symbol, out var rs)) continue;
            var price = todayPrices.TryGetValue(symbol, out var p) ? p : lot.AverageEntryPrice;
            // The candidate slice is already bounded to "up to today" — DateAlignedCloses only pairs
            // dates present in BOTH series, so passing this position's FULL bar history here can never
            // leak a future date into the correlation window regardless of how far its own series runs.
            existing.Add(new CorrelatedPosition(symbol, rs.Context.Bars, lot.Quantity * price));
        }
        return CorrelationClusterCalculator.CalculateClusterExposure(candidateBarsSlice, existing, riskOptions);
    }

    private static decimal ComputeAdtv(IReadOnlyList<HistoricalBar> bars, int idx, int lookbackDays)
    {
        var start = Math.Max(0, idx - lookbackDays + 1);
        var window = bars.Skip(start).Take(idx - start + 1).ToList();
        return window.Count == 0 ? 0m : window.Average(b => b.Close * b.Volume);
    }

    private static Position ToPosition(OpenPositionLot lot, string symbol) => new()
    {
        Symbol = symbol,
        Quantity = lot.Quantity,
        AverageEntryPrice = lot.AverageEntryPrice,
        InitialStop = lot.InitialStop,
        CurrentStop = lot.CurrentStop,
        InitialRiskAmount = lot.InitialRiskAmount,
        RiskPerShare = lot.RiskPerShare,
        HighestStageFilled = lot.HighestStageFilled,
        IsOpen = true,
        OpenedAt = lot.OpenedAt,
    };

    /// <summary>
    /// Caps a 0-based-index "up to and including idx" slice to at most
    /// maxLookback of the most recent items (null = unbounded, i.e. every
    /// item from the start through idx — identical to the pre-Tranche-18
    /// `.Take(idx + 1).ToList()` this replaces at each call site). See
    /// BacktestOptions.BoundedLookbackWindowBars for why this exists and
    /// why it defaults to unbounded.
    /// </summary>
    private static IReadOnlyList<T> BoundedSlice<T>(IReadOnlyList<T> items, int idx, int? maxLookback)
    {
        var count = idx + 1;
        if (maxLookback is null || count <= maxLookback.Value)
            return items.Take(count).ToList();

        return items.Skip(count - maxLookback.Value).Take(maxLookback.Value).ToList();
    }

    /// <summary>Binary search for the last date &lt;= target in an ascending, bars-parallel date list — "as of date" slicing shared by every no-look-ahead engine call in this file.</summary>
    private static IReadOnlyList<HistoricalBar>? SliceUpToOrBefore(IReadOnlyList<HistoricalBar> bars, IReadOnlyList<DateOnly> datesAscending, DateOnly date, int? maxLookback = null)
    {
        int lo = 0, hi = datesAscending.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (datesAscending[mid] <= date) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? null : BoundedSlice(bars, found, maxLookback);
    }

    private static BacktestRun BuildBacktestRun(
        BacktestRunRequest request, BacktestPortfolioState portfolioState, IReadOnlyList<PortfolioSnapshot> dailySnapshots,
        DrawdownState? drawdownState, DateTimeOffset startedAt, BacktestEngineOptions options)
    {
        var finalCapital = dailySnapshots.Count > 0 ? dailySnapshots[^1].Nav : options.Portfolio.InitialCapital;
        var totalDays = (request.EndDate.DayNumber - request.StartDate.DayNumber);
        decimal? cagr = totalDays > 0 && finalCapital > 0 && options.Portfolio.InitialCapital > 0
            ? (Pow((double)(finalCapital / options.Portfolio.InitialCapital), 365.0 / totalDays) - 1.0) is var g && !double.IsNaN(g) && !double.IsInfinity(g)
                ? (decimal)g * 100m
                : null
            : null;

        var analytics = TradeAnalyticsCalculator.Calculate(portfolioState.ClosedTrades);
        var (sharpe, sortino) = ComputeSharpeSortino(dailySnapshots.Select(s => s.Nav).ToList(), options.Indicator.TradingDaysPerYear);

        return new BacktestRun
        {
            StrategyVersion = request.StrategyVersion,
            ConfigurationHash = request.ConfigurationHash,
            DataVersion = request.DataVersion,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = BacktestRunStatus.Completed,
            SurvivorshipBiased = request.SurvivorshipBiased,
            FinalCapital = finalCapital,
            Cagr = cagr,
            MaxDrawdownPercent = drawdownState?.MaxDrawdownPercent,
            Sharpe = sharpe,
            Sortino = sortino,
            WinRatePercent = analytics.WinRatePercent,
            ProfitFactor = analytics.ProfitFactor,
            ExpectancyR = analytics.ExpectancyR,
            TradeCount = analytics.TradeCount,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    private static double Pow(double x, double y) => Math.Pow(x, y);

    /// <summary>
    /// Daily-return Sharpe/Sortino, annualized by sqrt(TradingDaysPerYear).
    /// Resolved simplification, documented: assumes a 0% risk-free rate
    /// (spec doesn't specify one) — Sharpe/Sortino are therefore both
    /// slightly optimistic relative to a risk-free-adjusted figure, a
    /// standard and clearly-stated simplification rather than an invented
    /// rate with no configured source.
    /// </summary>
    private static (decimal? Sharpe, decimal? Sortino) ComputeSharpeSortino(IReadOnlyList<decimal> navSeries, int tradingDaysPerYear)
    {
        if (navSeries.Count < 3) return (null, null);

        var returns = new List<decimal>(navSeries.Count - 1);
        for (var i = 1; i < navSeries.Count; i++)
            if (navSeries[i - 1] > 0) returns.Add((navSeries[i] - navSeries[i - 1]) / navSeries[i - 1]);

        if (returns.Count < 2) return (null, null);

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var stdDev = (decimal)Math.Sqrt((double)variance);

        var downside = returns.Where(r => r < 0).ToList();
        decimal? sortino = null;
        if (downside.Count >= 2)
        {
            var downsideVariance = downside.Sum(r => r * r) / (downside.Count - 1);
            var downsideStdDev = (decimal)Math.Sqrt((double)downsideVariance);
            sortino = downsideStdDev > 0 ? mean / downsideStdDev * (decimal)Math.Sqrt(tradingDaysPerYear) : null;
        }

        var sharpe = stdDev > 0 ? mean / stdDev * (decimal)Math.Sqrt(tradingDaysPerYear) : (decimal?)null;
        return (sharpe, sortino);
    }

    private sealed class SymbolRuntimeState
    {
        public required BacktestSymbolContext Context { get; init; }
        public required IReadOnlyList<IndicatorSnapshot> Indicators { get; init; }
        public required IReadOnlyDictionary<DateOnly, int> BarIndexByDate { get; init; }
        public IReadOnlyList<DateOnly>? SectorBenchmarkDates { get; init; }
        public IReadOnlyDictionary<DateOnly, List<HistoricalBar>> HourlyByDate { get; init; } = new Dictionary<DateOnly, List<HistoricalBar>>();
        public IReadOnlyDictionary<DateOnly, List<HistoricalBar>> QuarterByDate { get; init; } = new Dictionary<DateOnly, List<HistoricalBar>>();
        public IReadOnlyDictionary<DateOnly, DeliverySnapshot> DeliveryByDate { get; init; } = new Dictionary<DateOnly, DeliverySnapshot>();
    }

    private sealed record PendingEntryOrder(
        PyramidStage Stage, decimal Quantity,
        decimal? PlannedInitialStop, decimal? PlannedRiskPerShare, decimal? PlannedTotalTargetQuantity,
        int RegimeScoreAtEntry, decimal StockScoreAtEntry, decimal RiskAtEntryPercent, decimal? RefinementLevel = null)
    {
        public decimal? PlannedInitialRiskAmount => PlannedRiskPerShare is { } r && PlannedTotalTargetQuantity is { } t ? r * t : null;
    }

    private sealed record PendingExitOrder(ExitReason Reason);
}
