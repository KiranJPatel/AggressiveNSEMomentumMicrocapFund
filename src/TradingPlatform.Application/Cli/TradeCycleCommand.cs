using System.Text.Json;
using TradingPlatform.Strategy.Entry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Execution.OrderManagement;
using TradingPlatform.Execution.Orchestration;
using TradingPlatform.Execution.Paper;
using TradingPlatform.Execution.Reconciliation;
using TradingPlatform.Execution.Validation;
using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Indicators;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Risk.Trailing;
using TradingPlatform.Scanner;
using TradingPlatform.Strategy.Exit;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Strategy.RelativeStrength;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- paper` / `dotnet run -- live` (spec §6, §7.3) — one full
/// trading cycle, shared verbatim between both CLI entries (Tranche 20).
/// Same seam LiveModeGate/OrderManager were already built around (Tranche
/// 12/15): the ONLY thing that differs between paper and live is which
/// IExecutionProvider gets constructed, decided by
/// LiveModeGate.EffectiveExecutionMode from LiveTradingOptions — Live
/// without AllowLiveTrading=true falls back to Paper automatically,
/// exactly as that gate was designed to. This command supplies everything
/// LiveTradingCycleEngine (Tranche 15) needs but doesn't decide itself:
/// EXIT intents from ExitEngine/TrailingStopCalculator (Tranche 9) run
/// per open position, ENTRY intents from ScannerEngine (Tranche 14) run
/// with a REAL ScannerPortfolioContext this time (Tranche 18's `scan`
/// passed null, since no live portfolio existed yet to size against).
///
/// Runs as ONE cycle per invocation (matching every other command in this
/// CLI — `scan`/`backtest`/`fetch` are all one-shot too), using the
/// latest cached daily bar as "today's quote" — a real continuous intraday
/// daemon is out of scope; an operator runs this once per trading day
/// (e.g. via an external cron/scheduler) exactly as `scan` already works.
///
/// Documented simplifications versus BacktestEngine's day-by-day
/// simulation, both because they're artifacts of EOD-only backtesting
/// that don't apply the same way to a periodic live cycle, not oversights:
///   - No next-cycle order queuing for non-StopLoss exits: backtest
///     queues MomentumFailure/FundamentalFailure/RegimeFailure exits for
///     the NEXT bar's open specifically to avoid same-day look-ahead bias
///     in a simulation replaying history. A live cycle isn't replaying
///     anything — "today" is genuinely now — so every exit reason submits
///     this same cycle, as a Market order.
///   - RecentOrderRejections/RecentSlippageBps (two of LiveTradingCycleEngine's
///     five kill-switch trigger sources) are passed as empty lists — a
///     genuine rolling window needs its own persistence this tranche
///     didn't build; the other three triggers (reconciliation, drawdown,
///     daily-loss-limit) are fully wired and real.
/// </summary>
public static class TradeCycleCommand
{
    private const int WarmupCalendarDays = 500;
    private const string DrawdownStateKey = "DrawdownState";
    private const string LossLimitStateKey = "LossLimitState";
    internal const string KillSwitchStateKey = "KillSwitchState";
    internal const string KillSwitchResetAtKey = "KillSwitchResetAt";
    private const int RejectionHistoryWindow = 25;

    public static async Task<int> RunAsync(
        string[] args, bool forcePaperMode,
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications, ISurveillanceRepository surveillance,
        IPositionRepository positionRepo, IOrderRepository orderRepo, ITradeRepository tradeRepo, ISignalRepository signalRepo,
        IRiskEventRepository riskEventRepo, IDrawdownEventRepository drawdownEventRepo, IPortfolioSnapshotRepository snapshotRepo, IEngineStateRepository engineStateRepo,
        IOptions<ScannerOptions> scannerOptions, IOptions<IndicatorOptions> indicatorOptions, IOptions<VcpOptions> vcpOptions, IOptions<RsOptions> rsOptions,
        IOptions<StockScoringOptions> stockScoringOptions, IOptions<StrategyOptions> strategyOptions, IOptions<MarketRegimeOptions> marketRegimeOptions,
        IOptions<ExitOptions> exitOptions, IOptions<RiskOptions> riskOptions, IOptions<PortfolioOptions> portfolioOptions, IOptions<BacktestOptions> backtestOptions,
        IOptions<BenchmarkOptions> benchmarkOptions, IOptions<LiveTradingOptions> liveTradingOptions, IOptions<ZerodhaOptions> zerodhaOptions,
        IServiceProvider provider, ILogger logger, CancellationToken cancellationToken = default,
        IMarketRegimeRepository? regimeRepo = null, IScanEvidenceRepository? evidenceRepo = null, IDeliveryDataRepository? deliveryRepo = null)
    {
        // Intraday entry refinement exists only in the backtest (it needs a live 60m/15m feed, which paper/live do not have). Refusing is safer
        // than silently trading at the open while the operator believes entries are being refined.
        if (strategyOptions.Value.SignalToOrderLag != SignalToOrderLag.NextOpen)
        {
            Console.Error.WriteLine($"Strategy:SignalToOrderLag is {strategyOptions.Value.SignalToOrderLag}, but intraday refinement is backtest-only — paper/live execute at the next open. Set it to NextOpen.");
            return 1;
        }

        var acknowledgeReset = args.Contains("--acknowledge-drawdown-reset");
        var risk = riskOptions.Value;
        // `dotnet run -- paper` always simulates, no matter what LiveTrading:Mode
        // says in config — only `dotnet run -- live` ever reads the configured
        // Mode/AllowLiveTrading pair through to LiveModeGate. This is the CLI
        // verb itself acting as an additional, independent safety gate on top
        // of LiveModeGate's own config-flag gate (Tranche 15) — two separate
        // things have to agree (the command you typed AND the config) before
        // a real order can ever go out, the same "impossible to get subtly
        // wrong" philosophy LiveModeGate was already built around.
        var live = forcePaperMode ? ForcePaperMode(liveTradingOptions.Value) : liveTradingOptions.Value;
        var portfolio = portfolioOptions.Value;

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-WarmupCalendarDays));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);

        var (broadBars, midcapBars) = await UniverseDataLoader.LoadBenchmarksAsync(bars, benchmarkOptions.Value, from, to, logger, cancellationToken);
        if (broadBars.Count == 0)
        {
            Console.Error.WriteLine($"No cached bars for the broad-index benchmark ({benchmarkOptions.Value.BroadIndexSymbol}). Run 'dotnet run -- fetch' first.");
            return 1;
        }
        var asOfDate = DateOnly.FromDateTime(broadBars[^1].Timestamp.Date);

        // Current-state lookups (classification / surveillance / fundamentals) use today's market date, not the last bar's — see ScanCycle.
        var lookupDate = ScanCycle.Max(asOfDate, RealtimeBarOverlay.MarketDate(DateTimeOffset.UtcNow, scannerOptions.Value));
        var loaded = await UniverseDataLoader.LoadActiveUniverseAsync(instruments, bars, classifications, surveillance, from, to, lookupDate, logger, cancellationToken, scannerOptions.Value.SurveillanceMaxStalenessDays);
        var loadedBySymbol = loaded.ToDictionary(s => s.Instrument.Symbol);

        // Last safe abort point: data is loaded, nothing has been decided, written or sent to the broker. Once exits/entries start, the
        // cycle runs to completion — cancelling between two order submissions could leave a broker order the journal never recorded.
        cancellationToken.ThrowIfCancellationRequested();

        // Spec §3.1 universe-wide regime inputs, computed ONCE over the full loaded universe (the real-time re-rank pass only sees a
        // shortlist, which must not be mistaken for the market) plus the live/paper journal for the breakout success rate.
        var breadthObservations = loaded.Select(l => BreadthObservation.FromBars(l.Bars, asOfDate)).Where(o => o is not null).Select(o => o!.Value).ToList();
        var universeBreadth = UniverseBreadthCalculator.Calculate(breadthObservations, marketRegimeOptions.Value.BreadthMinimumSampleSize);
        var closedTradeHistory = await tradeRepo.GetLiveTradesAsync(cancellationToken);
        var breakoutSuccessRate = BreakoutSuccessRateCalculator.Calculate(closedTradeHistory, asOfDate, marketRegimeOptions.Value);

        var latestSnapshot = await snapshotRepo.GetLatestAsync(cancellationToken);
        var nav = latestSnapshot?.Nav ?? portfolio.InitialCapital;
        var cash = latestSnapshot?.CashBalance ?? portfolio.InitialCapital;

        var previousDrawdownState = await LoadStateAsync<DrawdownState>(engineStateRepo, DrawdownStateKey, cancellationToken);
        var previousLossLimitState = await LoadStateAsync<LossLimitState>(engineStateRepo, LossLimitStateKey, cancellationToken);
        var previousKillSwitchState = await LoadStateAsync<KillSwitchState>(engineStateRepo, KillSwitchStateKey, cancellationToken) ?? KillSwitchState.Inactive;
        var notifier = provider.GetService<INotificationService>();

        // Live only: learn the fate of orders submitted in earlier cycles BEFORE positions/cash are read, so this
        // cycle's exit evaluation, sizing and reconciliation see the real book (spec §6.3).
        if (LiveModeGate.EffectiveExecutionMode(live) == TradingMode.Live)
        {
            var syncOutcome = await TrySyncLiveOrdersAsync(provider, zerodhaOptions.Value, orderRepo, positionRepo, tradeRepo, signalRepo, riskEventRepo, engineStateRepo, notifier, logger, cancellationToken);
            if (syncOutcome is { } so && so.CashDelta != 0m) cash += so.CashDelta;
        }

        // Spec §6.4: a latched kill switch requires explicit manual intervention to clear. KillSwitchEngine.Reset()
        // existed since Tranche 15 but nothing could ever call it, so a latched state was un-clearable without
        // hand-editing SQLite. This flag (mirroring --acknowledge-drawdown-reset) is that manual intervention.
        // The reset timestamp is also persisted: it is the watermark after which order rejections count toward the
        // repeated-rejection detector, so the same old rejections cannot immediately re-trip a freshly reset switch.
        if (args.Contains("--acknowledge-kill-switch-reset") && previousKillSwitchState.IsActive)
        {
            var clearedFrom = previousKillSwitchState;
            previousKillSwitchState = KillSwitchEngine.Reset();
            await SaveStateAsync(engineStateRepo, KillSwitchStateKey, previousKillSwitchState, cancellationToken);
            await SaveStateAsync(engineStateRepo, KillSwitchResetAtKey, DateTimeOffset.UtcNow, cancellationToken);
            await riskEventRepo.InsertAsync(new RiskEvent
            {
                EventType = "KillSwitchReset",
                Description = $"Manually reset (was {clearedFrom.Source}: {clearedFrom.Reason})",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
            }, cancellationToken);
            if (notifier is not null)
                await notifier.NotifyAsync(NotificationCategories.KillSwitch, $"Kill switch manually reset (was {clearedFrom.Source}: {clearedFrom.Reason}).", cancellationToken);
        }

        var (drawdownState, drawdownEvent) = DrawdownEngine.Evaluate(previousDrawdownState, asOfDate, nav, risk, acknowledgeReset);
        var lossLimitState = PeriodicLossLimitEngine.Evaluate(previousLossLimitState, asOfDate, nav, risk);
        var riskGate = PortfolioRiskGate.Evaluate(drawdownState, lossLimitState, risk);

        var openPositions = await positionRepo.GetOpenAsync(cancellationToken);

        var regimeSnapshot = MarketRegimeEngine.Calculate(
            new MarketRegimeInputs
            {
                BroadIndexBars = broadBars, MidcapIndexBars = midcapBars,
                PercentUniverseAbove50Dma = universeBreadth.PercentAbove50Dma, PercentUniverseAbove200Dma = universeBreadth.PercentAbove200Dma,
                AdvanceDeclineRatio = universeBreadth.AdvanceDeclineRatio, BreakoutSuccessRatePercent = breakoutSuccessRate,
            },
            marketRegimeOptions.Value, rsOptions.Value, asOfDate);
        if (regimeRepo is not null) await regimeRepo.UpsertAsync(regimeSnapshot, cancellationToken);

        // ---------------- exits (and trailing-stop updates for positions that DON'T exit) ----------------

        var exitIntents = new List<(Order Order, OrderValidationInputs Validation, Position Position, ExitReason Reason)>();
        var stopTrailUpdates = new List<Position>();
        var excursionsBySymbol = new Dictionary<string, (decimal MfeR, decimal MaeR)>();
        // Conservative baseline (entry-price based; trailing only ever shrinks it) for the pyramid-add heat check.
        var heatBaseline = openPositions.Sum(p => Math.Max(0m, p.Quantity * (p.AverageEntryPrice - p.CurrentStop)));
        var addIntents = new List<(Order Order, OrderValidationInputs Validation, Position Position, AddPlan Plan)>();
        var openOrderSymbols = (await orderRepo.GetOpenOrdersAsync(cancellationToken)).Select(o => o.Symbol).ToHashSet();
        var currentPrices = new Dictionary<string, decimal>();

        // ---- Real-time data (spec §7.3) ----
        // While the NSE session is open, daily bars alone are a day stale: stops, breakouts and paper fills would all use yesterday's close. So
        // today's forming bar is built from a live quote and laid over the cached final history. Outside market hours (or with --no-realtime /
        // Realtime:Enabled=false) nothing changes. A symbol whose quote is stale/implausible/inconsistent gets NO overlay and is excluded from new
        // entries — the failure mode is "yesterday's data or nothing", never a guessed price.
        var realtimeOptions = provider.GetService<IOptions<RealtimeOptions>>()?.Value ?? new RealtimeOptions();
        RealtimeSession? rt = null;
        string? realtimeNote = null;
        if (args.Contains("--no-realtime")) realtimeNote = "disabled by --no-realtime";
        else if (!realtimeOptions.Enabled) realtimeNote = "disabled in config (Realtime:Enabled=false)";
        else if (!(args.Contains("--realtime-assume-open") || TradingPlatform.Scanner.Scheduling.MarketHoursCalculator.IsMarketOpen(DateTimeOffset.UtcNow, scannerOptions.Value)))
            realtimeNote = "market closed — using final daily bars";
        else
        {
            var rtProvider = provider.GetService<IRealtimeQuoteProvider>() ?? RealtimeProviderFactory.Create(provider, realtimeOptions, zerodhaOptions.Value, logger, out realtimeNote);
            if (rtProvider is not null) rt = new RealtimeSession(rtProvider, realtimeOptions, scannerOptions.Value, logger);
            else if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.SystemFailure, $"Real-time data unavailable ({realtimeNote}) — this cycle uses daily bars only.", cancellationToken);
        }

        var positionBarsCache = new Dictionary<string, IReadOnlyList<HistoricalBar>>();
        foreach (var p in openPositions) positionBarsCache[p.Symbol] = await ResolveBarsAsync(p.Symbol, loadedBySymbol, bars, from, to, cancellationToken);
        if (rt is not null) await rt.RefreshAsync(openPositions.Select(p => p.Symbol), sym => positionBarsCache.GetValueOrDefault(sym) ?? Array.Empty<HistoricalBar>(), cancellationToken);

        foreach (var position in openPositions)
        {
            var positionBars = positionBarsCache[position.Symbol];
            if (rt is not null && positionBars.Count > 0)
            {
                if (rt.OverlayBars(position.Symbol) is { } liveBars) positionBars = liveBars;
                else logger.LogWarning("trade-cycle: {Symbol} evaluated on DAILY bars only — {Why}.", position.Symbol, rt.WhyNoOverlay(position.Symbol));
            }
            if (positionBars.Count == 0)
            {
                logger.LogWarning("trade-cycle: no cached bars for open position {Symbol} — left untouched this cycle.", position.Symbol);
                continue;
            }

            var lastBar = positionBars[^1];
            currentPrices[position.Symbol] = lastBar.Close;

            var indicatorSeries = IndicatorEngine.ComputeSeries(positionBars, indicatorOptions.Value);
            var lastIndicators = indicatorSeries[^1];
            var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>> { [BenchmarkNames.BroadIndex] = broadBars };
            if (midcapBars is { Count: > 0 }) benchmarks[BenchmarkNames.Midcap] = midcapBars;
            var rsResult = RelativeStrengthEngine.Calculate(positionBars, benchmarks, rsOptions.Value);

            // No fundamentals ingestion pipeline exists anywhere in this codebase (see UniverseDataLoader's remarks) —
            // FundamentalFailure simply never fires, the same "absent means unavailable, not a fabricated pass" ExitEngine already documents.
            var exitFundamentalProvider = provider.GetService<IFundamentalDataProvider>();
            var (exitFundamentals, _, _) = await ScannerEnrichment.LoadAsync(exitFundamentalProvider, null, position.Symbol, lookupDate, cancellationToken);
            FundamentalSnapshot? fundamentals = exitFundamentals; // null (mechanism not evaluated) unless fundamentals were actually imported

            var exitEval = ExitEngine.Evaluate(position, lastBar, lastIndicators, rsResult.BlendedScore, regimeSnapshot.Classification, fundamentals, exitOptions.Value);

            if (exitEval.ShouldExit)
            {
                var order = new Order
                {
                    Symbol = position.Symbol, Side = OrderSide.Sell, Type = OrderType.Market,
                    Quantity = position.Quantity, CorrelationId = Guid.NewGuid().ToString(),
                };
                var validation = new OrderValidationInputs
                {
                    CandidateOrder = order,
                    IsMarketOpen = true,
                    IsInstrumentActive = true,
                    IsSymbolMapped = true,
                    AvailableCash = cash,
                    EstimatedOrderValue = 0m, // a risk-reducing sell's value is never a binding constraint (spec §6.4's exit carve-out) — see OrderValidationEngine
                    WouldExceedPositionLimit = false,
                    WouldExceedSectorLimit = false,
                    WouldExceedMicrocapLimit = false,
                    ProjectedPortfolioHeatValue = 0m,
                    PortfolioHeatMaxValue = PortfolioHeatCap(nav, risk),
                    TradingIsShutDown = riskGate.BlocksNewEntries,
                    DailyLossLimitBreached = lossLimitState.DailyLimitBreached,
                    MaxFillableQuantity = position.Quantity,
                    IsDuplicateOrder = false,
                    HasUnexpectedExistingPosition = false,
                    HasPendingOrderInSymbol = false,
                };
                excursionsBySymbol[position.Symbol] = IntentFactory.Excursions(position, positionBars);
                exitIntents.Add((order, validation, position, exitEval.PrimaryReason!.Value));
            }
            else
            {
                var trailing = TrailingStopCalculator.Calculate(position, positionBars, lastIndicators, strategyOptions.Value, exitOptions.Value);
                position.CurrentStop = trailing.NewStop;
                stopTrailUpdates.Add(position);

                // Pyramid add (spec §3.7). Never while new entries are blocked (drawdown/kill switch/loss limit), never with an
                // order already pending in the symbol, and only if the add is affordable.
                var stagingJson = await engineStateRepo.GetAsync(PositionStaging.StateKey(position.Symbol), cancellationToken);
                var staging = stagingJson is null ? null : JsonSerializer.Deserialize<PositionStaging>(stagingJson);
                if (staging is not null && !riskGate.BlocksNewEntries && !drawdownState.IsTradingShutdown && !previousKillSwitchState.IsActive
                    && !lossLimitState.DailyLimitBreached && !openOrderSymbols.Contains(position.Symbol))
                {
                    var plan = StagedAddPlanner.Plan(position, staging, positionBars, lastIndicators, strategyOptions.Value);
                    if (plan is not null && plan.Quantity * lastBar.Close <= cash)
                    {
                        var addOrder = new Order
                        {
                            Symbol = position.Symbol, Side = OrderSide.Buy, Type = OrderType.Market,
                            Quantity = plan.Quantity, CorrelationId = Guid.NewGuid().ToString(),
                        };
                        addIntents.Add((addOrder, new OrderValidationInputs
                        {
                            CandidateOrder = addOrder, IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
                            AvailableCash = cash, EstimatedOrderValue = plan.Quantity * lastBar.Close,
                            WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
                            ProjectedPortfolioHeatValue = heatBaseline + Math.Max(0m, plan.Quantity * (lastBar.Close - position.CurrentStop)), PortfolioHeatMaxValue = PortfolioHeatCap(nav, risk),
                            TradingIsShutDown = riskGate.BlocksNewEntries, DailyLossLimitBreached = lossLimitState.DailyLimitBreached,
                            MaxFillableQuantity = plan.Quantity, IsDuplicateOrder = false,
                            HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
                        }, position, plan));
                    }
                }
            }
        }

        // ---------------- entries (via ScannerEngine, with a real portfolio context) ----------------

        var openSymbols = openPositions.Select(p => p.Symbol).ToHashSet();
        var sectorExposure = ComputeSectorExposure(openPositions, currentPrices, loadedBySymbol);
        var microcapExposure = ComputeBucketExposure(openPositions, currentPrices, loadedBySymbol, MarketCapBucket.Micro);
        var heatValue = openPositions.Sum(p => Math.Max(0m, p.Quantity * (currentPrices.GetValueOrDefault(p.Symbol, p.AverageEntryPrice) - p.CurrentStop)));
        var correlationPositions = openPositions
            .Where(p => loadedBySymbol.ContainsKey(p.Symbol))
            .Select(p => new TradingPlatform.Risk.Correlation.CorrelatedPosition(p.Symbol, loadedBySymbol[p.Symbol].Bars, p.Quantity * currentPrices.GetValueOrDefault(p.Symbol, p.AverageEntryPrice)))
            .ToList();

        var scannerPortfolioContext = new ScannerPortfolioContext
        {
            Nav = nav,
            CashBalance = cash,
            DeployableCapitalPercentFromDrawdownAxis = riskGate.DeployableCapitalPercent,
            CurrentPortfolioHeatValue = heatValue,
            CurrentMicrocapExposureValue = microcapExposure,
            CurrentSectorExposureValueBySector = sectorExposure,
            ExistingOpenPositionsForCorrelation = correlationPositions,
        };

        var fundamentalProvider = provider.GetService<IFundamentalDataProvider>();
        var institutionalProvider = provider.GetService<IInstitutionalActivityProvider>();
        var scannerSymbolContexts = new List<ScannerSymbolContext>(loaded.Count);
        foreach (var s in loaded)
        {
            var (fund, inst, priorFund) = await ScannerEnrichment.LoadAsync(fundamentalProvider, institutionalProvider, s.Instrument.Symbol, lookupDate, cancellationToken);
            scannerSymbolContexts.Add(new ScannerSymbolContext
            {
                Instrument = s.Instrument, Bars = s.Bars, Surveillance = s.Surveillance, Classification = s.Classification,
                Fundamentals = fund, PriorFundamentals = priorFund, InstitutionalActivity = inst,
                DeliveryRecords = await DeliveryContextLoader.LoadForScanAsync(deliveryRepo, s.Instrument.Symbol, asOfDate, stockScoringOptions.Value, strategyOptions.Value, cancellationToken),
            });
        }

        var scannerEngineOptions = new ScannerEngineOptions
        {
            Scanner = scannerOptions.Value, Indicator = indicatorOptions.Value, Vcp = vcpOptions.Value, Rs = rsOptions.Value,
            StockScoring = stockScoringOptions.Value, Strategy = strategyOptions.Value, MarketRegime = marketRegimeOptions.Value,
            Risk = risk, Portfolio = portfolio, RecentClosedTrades = closedTradeHistory, PrecomputedBreadth = universeBreadth,
        };

        var liveCloseBySymbol = new Dictionary<string, decimal>();   // today's overlay price per shortlisted symbol (real-time pass only)
        IReadOnlyList<ScannerResult> scanResults;
        IReadOnlyList<ScanEvidence> scanEvidence = Array.Empty<ScanEvidence>();
        if (riskGate.BlocksNewEntries || drawdownState.IsTradingShutdown)
            scanResults = Array.Empty<ScannerResult>(); // trading shutdown means literally no new entries are even evaluated, not just size-zeroed (spec §4.5)
        else
        {
            var pass1 = ScannerEngine.ScanDetailed(asOfDate, broadBars, midcapBars, scannerSymbolContexts, scannerPortfolioContext, scannerEngineOptions);
            scanResults = pass1.Results;
            scanEvidence = pass1.Evidence;
            if (rt is not null && scanResults.Count > 0)
            {
                // Pass 2: quote only the pass-1 shortlist, lay today's forming bar over each, and RE-RANK those — so a symbol breaking out
                // right now is judged on today's price/volume, not yesterday's. Shortlisted symbols without a valid live quote are dropped
                // from entry consideration (we will not buy on stale data while the market is moving).
                var shortlist = scanResults.Select(r => r.Symbol).ToList();
                await rt.RefreshAsync(shortlist, sym => loadedBySymbol.TryGetValue(sym, out var l) ? l.Bars : Array.Empty<HistoricalBar>(), cancellationToken);
                var liveContexts = new List<ScannerSymbolContext>();
                foreach (var c in scannerSymbolContexts)
                {
                    if (!shortlist.Contains(c.Instrument.Symbol)) continue;
                    if (rt.OverlayBars(c.Instrument.Symbol) is not { } liveBarsForSymbol) continue;
                    liveContexts.Add(new ScannerSymbolContext
                    {
                        Instrument = c.Instrument, Bars = liveBarsForSymbol, SectorBenchmarkBars = c.SectorBenchmarkBars, Fundamentals = c.Fundamentals,
                        PriorFundamentals = c.PriorFundamentals, InstitutionalActivity = c.InstitutionalActivity, Surveillance = c.Surveillance, Classification = c.Classification,
                        // The overlay bar is today's still-forming session; delivery is published after the close, so use the last COMPLETED session's.
                        DeliveryRecords = c.DeliveryRecords, DeliveryAsOfDate = DateOnly.FromDateTime(c.Bars[^1].Timestamp.Date),
                    });
                }
                foreach (var lc in liveContexts) liveCloseBySymbol[lc.Instrument.Symbol] = lc.Bars[^1].Close;
                var pass2 = ScannerEngine.ScanDetailed(asOfDate, broadBars, midcapBars, liveContexts, scannerPortfolioContext, scannerEngineOptions);
                scanResults = pass2.Results;
                scanEvidence = pass2.Evidence;
            }
            if (evidenceRepo is not null) await evidenceRepo.UpsertAsync(scanEvidence, cancellationToken);
        }

        var availableSlots = Math.Max(0, portfolio.MaxPositions - openPositions.Count);
        var entryCandidates = scanResults
            .Where(r => r.BreakoutStatus == "Breakout confirmed" && r.SuggestedPositionSize > 0 && !openSymbols.Contains(r.Symbol))
            .Take(availableSlots)
            .ToList();

        var entryIntents = new List<(Order Order, OrderValidationInputs Validation, ScannerResult Candidate)>();
        foreach (var candidate in entryCandidates)
        {
            // Spec §3.7: enter ONLY Stage 1 (40%) now; Stage 2/3 are added later by StagedAddPlanner when their triggers pass.
            var stage1Qty = StagedEntryCalculator.Split(candidate.SuggestedPositionSize, strategyOptions.Value).Stage1Qty;
            if (stage1Qty <= 0) continue; // total target too small to stage — skip rather than silently buying the whole thing

            // Gap-risk guard (spec §4 risk limits): sizing assumed an entry at the pivot, but a market order fills at the current price, which for a
            // breakout is often well above it — and the stop (derived from the pivot) then sits much further below the real fill. Scale the entry down
            // so rupee risk stays at the planned budget (never up); skip it entirely if price is already at/through the stop.
            if (risk.ResizeEntryForGapRisk && candidate.SuggestedStop > 0m && candidate.SuggestedEntry > candidate.SuggestedStop)
            {
                var expectedFill = liveCloseBySymbol.TryGetValue(candidate.Symbol, out var liveClose) ? liveClose
                    : loadedBySymbol.TryGetValue(candidate.Symbol, out var loadedSym) && loadedSym.Bars.Count > 0 ? loadedSym.Bars[^1].Close : candidate.SuggestedEntry;
                var gap = TradingPlatform.Risk.Sizing.GapRiskAdjuster.Adjust(stage1Qty, candidate.SuggestedPositionSize, candidate.SuggestedEntry, candidate.SuggestedStop, expectedFill);
                if (gap.Cancelled)
                {
                    Console.WriteLine($"  SKIP  {candidate.Symbol,-12} price {expectedFill:N2} is at/through the stop {candidate.SuggestedStop:N2} — no valid entry");
                    continue;
                }
                if (gap.Resized)
                {
                    Console.WriteLine($"  GAP   {candidate.Symbol,-12} expected fill {expectedFill:N2} is {(expectedFill / candidate.SuggestedEntry - 1m) * 100m:F1}% above the planned entry {candidate.SuggestedEntry:N2}: entry scaled {stage1Qty:N0} -> {gap.Stage1Quantity:N0} to hold risk at budget");
                    stage1Qty = gap.Stage1Quantity;
                    candidate.SuggestedPositionSize = gap.TotalTargetQuantity;
                    candidate.Risk = gap.TotalTargetQuantity * gap.ActualRiskPerShare;
                }
            }
            var order = new Order
            {
                Symbol = candidate.Symbol, Side = OrderSide.Buy, Type = OrderType.Market,
                Quantity = stage1Qty, CorrelationId = Guid.NewGuid().ToString(),
            };
            var estimatedValue = stage1Qty * candidate.SuggestedEntry;
            var validation = new OrderValidationInputs
            {
                CandidateOrder = order,
                IsMarketOpen = true,
                IsInstrumentActive = true,
                IsSymbolMapped = true,
                AvailableCash = cash,
                EstimatedOrderValue = estimatedValue,
                // Already enforced inside ScannerEngine's own PositionSizingCalculator call (sizing.Rejected would have
                // zeroed SuggestedPositionSize, filtering this candidate out above) — re-checking here would be redundant.
                WouldExceedPositionLimit = false,
                WouldExceedSectorLimit = false,
                WouldExceedMicrocapLimit = false,
                ProjectedPortfolioHeatValue = heatValue + candidate.Risk,
                PortfolioHeatMaxValue = PortfolioHeatCap(nav, risk),
                TradingIsShutDown = riskGate.BlocksNewEntries,
                DailyLossLimitBreached = lossLimitState.DailyLimitBreached,
                MaxFillableQuantity = stage1Qty,
                IsDuplicateOrder = false,
                HasUnexpectedExistingPosition = await positionRepo.GetOpenBySymbolAsync(candidate.Symbol, cancellationToken) is not null,
                HasPendingOrderInSymbol = false,
            };
            entryIntents.Add((order, validation, candidate));
        }

        var isDryRun = live.Mode == TradingMode.DryRun;

        Console.WriteLine("========================================================");
        Console.WriteLine($" TRADE CYCLE — {live.Mode} — as of {asOfDate:yyyy-MM-dd}");
        Console.WriteLine("========================================================");
        Console.WriteLine($"NAV={nav:N2}  Cash={cash:N2}  OpenPositions={openPositions.Count}  Drawdown={drawdownState.DrawdownPercent:F1}% (tier {drawdownState.CurrentTier.Label})  DeployableCapital={riskGate.DeployableCapitalPercent:F0}%");
        Console.WriteLine(rt is not null ? $"Data: REAL-TIME overlay — {rt.Summary()}" : $"Data: daily bars (real-time {realtimeNote ?? "off"}).");
        foreach (var (symbol, reason) in (rt?.Problems ?? Array.Empty<(string, string)>()).Take(8)) Console.WriteLine($"  no live data for {symbol}: {reason}");
        if (rt is not null && rt.Requested > 0 && rt.ValidCount == 0 && notifier is not null)
            await notifier.NotifyAsync(NotificationCategories.SystemFailure, $"Real-time: none of {rt.Requested} quotes were usable ({rt.SourceName}) — cycle ran on daily bars; entries need live data and were suppressed.", cancellationToken);
        if (riskGate.BlocksNewEntries) Console.WriteLine($"NEW ENTRIES BLOCKED: {string.Join(", ", riskGate.BlockingReasons)}");
        Console.WriteLine($"Exits this cycle: {exitIntents.Count}   Stops trailed (no exit): {stopTrailUpdates.Count}   New entries proposed: {entryIntents.Count}   Pyramid adds: {addIntents.Count}");
        foreach (var (order, _, position, reason) in exitIntents)
            Console.WriteLine($"  EXIT  {order.Symbol,-12} qty={order.Quantity:N0} reason={reason} (entry={position.AverageEntryPrice:N2}, stop={position.CurrentStop:N2})");
        foreach (var (order, _, _, plan) in addIntents)
            Console.WriteLine($"  ADD   {order.Symbol,-12} qty={order.Quantity:N0} {plan.Stage}");
        foreach (var (order, _, candidate) in entryIntents)
            Console.WriteLine($"  ENTRY {order.Symbol,-12} qty={order.Quantity:N0} @~{candidate.SuggestedEntry:N2} stop={candidate.SuggestedStop:N2} risk={candidate.Risk:N2}");

        if (isDryRun)
        {
            Console.WriteLine();
            Console.WriteLine("DryRun mode — nothing submitted, nothing persisted. Set LiveTrading:Mode to Paper or Live to actually trade.");
            Console.WriteLine("========================================================");
            return 0;
        }

        // ---------------- execution provider (Paper by default; Live only when LiveModeGate clears) ----------------

        var effectiveMode = LiveModeGate.EffectiveExecutionMode(live);
        // Paper fills price off the LIVE quote when a real-time overlay exists for the symbol; otherwise off the last cached bar (and that is reported).
        IQuoteSource cachedQuotes = new CachedBarQuoteSource(loadedBySymbol);
        var realtimeQuotes = rt is null ? null : new RealtimeQuoteSource(cachedQuotes, rt);
        IQuoteSource paperQuotes = realtimeQuotes ?? cachedQuotes;
        IExecutionProvider executionProvider;
        if (effectiveMode == TradingMode.Live)
        {
            var built = TryBuildLiveExecutionProvider(provider, zerodhaOptions.Value, logger);
            if (built is null)
            {
                Console.WriteLine();
                Console.WriteLine("Live execution unavailable (missing credentials or broker error) — falling back to Paper simulation for this cycle.");
                executionProvider = BuildPaperExecutionProvider(paperQuotes, backtestOptions.Value, risk);
                effectiveMode = TradingMode.Paper;
            }
            else
            {
                executionProvider = built;
            }
        }
        else
        {
            executionProvider = BuildPaperExecutionProvider(paperQuotes, backtestOptions.Value, risk);
        }

        var orderManager = new OrderManager(executionProvider);
        var cycleEngine = new LiveTradingCycleEngine(orderManager);

        // Reconciliation only means something against a real broker; in Paper there is nothing external to compare
        // against, so local state stands in for both sides (an automatic "clean" result) — same principle as ReconcileCommand.
        IReadOnlyList<Position> brokerPositions = openPositions;
        var brokerCash = cash;
        if (effectiveMode == TradingMode.Live)
        {
            var reconciled = await TryFetchBrokerStateAsync(provider, zerodhaOptions.Value, logger, cancellationToken);

            // Spec §6.5 restart-recovery gate, now actually on the live path (RestartRecoveryEngine was previously
            // exercised only by its own unit tests). Before: if the broker fetch failed, local state was reconciled
            // against ITSELF and always came back "clean" — i.e. a broker outage looked identical to a healthy
            // account and orders could still go out. Now an unreachable/unvalidated broker HALTS the cycle with no
            // orders submitted. Drawdown shutdown is deliberately NOT passed in (null): PortfolioRiskGate already
            // blocks new entries for it, and halting here would also block the risk-reducing exits that a shutdown
            // most needs to allow.
            var recovery = RestartRecoveryEngine.Evaluate(
                reconciled is not null, openPositions, reconciled?.Positions ?? openPositions,
                cash, reconciled?.Cash ?? cash, drawdownState: null, live.CashReconciliationToleranceRupees);

            if (reconciled is null)
            {
                const string haltMessage = "LIVE cycle halted: broker state could not be fetched/validated — no orders submitted. Fix connectivity/credentials and re-run.";
                Console.Error.WriteLine(haltMessage);
                logger.LogError("trade-cycle: {Message}", haltMessage);
                if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.SystemFailure, haltMessage, cancellationToken);
                return 2;
            }

            (brokerPositions, brokerCash) = reconciled.Value;
            if (!recovery.SafeToResume && notifier is not null)
                await notifier.NotifyAsync(NotificationCategories.ReconciliationFailure, recovery.HaltReason ?? "Reconciliation mismatch on restart.", cancellationToken);
        }

        // Spec §6.4 "repeated order rejection" kill-switch source: previously fed an always-empty list, so the
        // detector could never fire from the CLI. Terminal outcomes of the most recent orders (oldest first),
        // counting only orders newer than the last manual kill-switch reset.
        var recentRejections = await BuildRecentRejectionHistoryAsync(orderRepo, engineStateRepo, cancellationToken);
        var recentSlippageBps = await BuildRecentSlippageHistoryAsync(orderRepo, engineStateRepo, cancellationToken);

        var cycleInputs = new LiveTradingCycleInputs
        {
            AsOfDate = asOfDate,
            Now = DateTimeOffset.UtcNow,
            Nav = nav,
            PreviousDrawdownState = previousDrawdownState,
            PreviousLossLimitState = previousLossLimitState,
            CurrentKillSwitchState = previousKillSwitchState,
            RiskOptions = risk,
            LiveTradingOptions = live,
            LocalOpenPositions = openPositions,
            BrokerOpenPositions = brokerPositions,
            LocalCashBalance = cash,
            BrokerCashBalance = brokerCash,
            RecentOrderRejections = recentRejections,
            RecentSlippageBps = recentSlippageBps,
            ExitIntents = exitIntents.Select(e => (e.Order, e.Validation)).ToList(),
            EntryIntents = entryIntents.Select(e => (e.Order, e.Validation)).Concat(addIntents.Select(a => (a.Order, a.Validation))).ToList(),
        };

        var cycleResult = await cycleEngine.RunCycleAsync(cycleInputs, cancellationToken);
        var killSwitchNewlyActivated = cycleResult.KillSwitchState.IsActive && !previousKillSwitchState.IsActive;

        var intentFactory = new IntentFactory(regimeSnapshot.RegimeScore, risk.RiskPercentPerTrade, excursionsBySymbol);
        await PersistCycleAsync(
            cycleResult, exitIntents, entryIntents, addIntents, stopTrailUpdates, intentFactory, drawdownEvent, drawdownState, killSwitchNewlyActivated, currentPrices, asOfDate, nav, cash, heatValue, regimeSnapshot.Classification,
            orderRepo, positionRepo, tradeRepo, signalRepo, riskEventRepo, drawdownEventRepo, snapshotRepo, engineStateRepo, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Reconciliation: {(cycleResult.Reconciliation.IsClean ? "clean" : $"DIRTY ({cycleResult.Reconciliation.Mismatches.Count} mismatch(es))")}");
        Console.WriteLine($"Kill switch: {(cycleResult.KillSwitchState.IsActive ? $"ACTIVE ({cycleResult.KillSwitchState.Source}: {cycleResult.KillSwitchState.Reason})" : "inactive")}");
        foreach (var outcome in cycleResult.SubmittedOrders)
            Console.WriteLine($"  {outcome.Order.Symbol,-12} {outcome.Order.Side} -> {outcome.Order.State} {(outcome.Accepted ? "" : $"({outcome.Order.RejectionReason})")}");
        Console.WriteLine("========================================================");

        if (notifier is not null)
        {
            foreach (var e in entryIntents)
                await notifier.NotifyAsync(NotificationCategories.Signal, $"Breakout entry proposed: {e.Order.Symbol} qty={e.Order.Quantity:N0} @~{e.Candidate.SuggestedEntry:N2} stop={e.Candidate.SuggestedStop:N2}", cancellationToken);
            foreach (var a in addIntents)
                await notifier.NotifyAsync(NotificationCategories.Signal, $"Pyramid add proposed: {a.Order.Symbol} {a.Plan.Stage} qty={a.Order.Quantity:N0}", cancellationToken);
            foreach (var x in exitIntents)
                await notifier.NotifyAsync(x.Reason is ExitReason.StopLoss or ExitReason.TrailingStopHit ? NotificationCategories.Stop : NotificationCategories.Order,
                    $"Exit proposed: {x.Order.Symbol} qty={x.Order.Quantity:N0} reason={x.Reason}", cancellationToken);
            foreach (var outcome in cycleResult.SubmittedOrders)
            {
                var o = outcome.Order;
                if (outcome.Accepted && o.State == OrderState.Filled)
                    await notifier.NotifyAsync(NotificationCategories.Fill, $"{o.Side} {o.Quantity:N0} {o.Symbol} filled.", cancellationToken);
                else if (!outcome.Accepted)
                    await notifier.NotifyAsync(NotificationCategories.Rejection, $"{o.Side} {o.Quantity:N0} {o.Symbol} rejected: {o.RejectionReason}", cancellationToken);
                else
                    await notifier.NotifyAsync(NotificationCategories.Order, $"{o.Side} {o.Quantity:N0} {o.Symbol} -> {o.State}.", cancellationToken);
            }
            if (drawdownEvent is not null)
                await notifier.NotifyAsync(NotificationCategories.Drawdown, $"Drawdown tier change — now {drawdownState.DrawdownPercent:F1}% (tier {drawdownState.CurrentTier.Label}).", cancellationToken);
            if (!cycleResult.Reconciliation.IsClean)
                await notifier.NotifyAsync(NotificationCategories.ReconciliationFailure, $"Reconciliation DIRTY: {cycleResult.Reconciliation.Mismatches.Count} mismatch(es) — trading halted pending manual review.", cancellationToken);
            if (killSwitchNewlyActivated)
                await notifier.NotifyAsync(NotificationCategories.KillSwitch, $"Kill switch ACTIVATED — {cycleResult.KillSwitchState.Source}: {cycleResult.KillSwitchState.Reason}", cancellationToken);
        }

        logger.LogInformation("trade-cycle: mode={Mode} exits={Exits} entries={Entries} killSwitchActive={KillSwitch} reconciliationClean={Reconciliation}.",
            effectiveMode, exitIntents.Count, entryIntents.Count, cycleResult.KillSwitchState.IsActive, cycleResult.Reconciliation.IsClean);
        return 0;
    }

    // ---------------- persistence ----------------

    /// <summary>
    /// Takes `drawdownState` (computed once, up front in RunAsync, with
    /// --acknowledge-drawdown-reset correctly applied) rather than
    /// `cycleResult.DrawdownState` for anything persisted: LiveTradingCycleEngine
    /// re-derives its own DrawdownState internally from the same
    /// PreviousDrawdownState (needed for its OWN kill-switch gating), but
    /// always calls DrawdownEngine.Evaluate with the default
    /// manualResetAcknowledged=false — it has no parameter for the CLI's
    /// acknowledge flag. When acknowledgeReset=false (the common case)
    /// the two are always identical (same deterministic inputs), so this
    /// only changes behavior in the one case it needs to: an operator
    /// explicitly clearing a shutdown latch.
    /// </summary>
    private static async Task PersistCycleAsync(
        LiveTradingCycleResult cycleResult,
        List<(Order Order, OrderValidationInputs Validation, Position Position, ExitReason Reason)> exitIntents,
        List<(Order Order, OrderValidationInputs Validation, ScannerResult Candidate)> entryIntents,
        List<(Order Order, OrderValidationInputs Validation, Position Position, AddPlan Plan)> addIntents,
        List<Position> stopTrailUpdates, IntentFactory intents,
        DrawdownEvent? drawdownEvent, DrawdownState drawdownState, bool killSwitchNewlyActivated,
        IReadOnlyDictionary<string, decimal> markPrices, DateOnly asOfDate, decimal navBeforeCycle, decimal cashBeforeCycle, decimal heatValueBeforeCycle, RegimeClassification regime,
        IOrderRepository orderRepo, IPositionRepository positionRepo, ITradeRepository tradeRepo, ISignalRepository signalRepo,
        IRiskEventRepository riskEventRepo, IDrawdownEventRepository drawdownEventRepo, IPortfolioSnapshotRepository snapshotRepo,
        IEngineStateRepository engineStateRepo, CancellationToken cancellationToken)
    {
        // Trailed-but-not-exited stops first — these positions are otherwise untouched by this cycle's submitted orders.
        foreach (var position in stopTrailUpdates)
            await positionRepo.UpsertAsync(position, cancellationToken);

        var cash = cashBeforeCycle;

        foreach (var outcome in cycleResult.SubmittedOrders)
        {
            var orderId = await orderRepo.InsertAsync(outcome.Order, cancellationToken);
            foreach (var evt in outcome.Events)
                await orderRepo.RecordStateTransitionAsync(orderId, evt.FromState, evt.ToState, outcome.Order.BrokerOrderId, outcome.Order.RejectionReason, evt.Detail, evt.Timestamp, cancellationToken);

            if (!outcome.Accepted) continue;

            // Accepted but not (fully) filled yet — the live case (Kite returns "Submitted"). Remember what the order was
            // for, so LiveOrderSync can apply the fill when the broker reports it in a later cycle.
            if (outcome.Order.State is not (OrderState.Filled or OrderState.PartiallyFilled))
            {
                var pendingExit = outcome.Order.Side == OrderSide.Sell ? exitIntents.FirstOrDefault(e => e.Order.CorrelationId == outcome.Order.CorrelationId) : default;
                var pendingEntry = outcome.Order.Side == OrderSide.Buy ? entryIntents.FirstOrDefault(e => e.Order.CorrelationId == outcome.Order.CorrelationId) : default;
                var pendingAdd = outcome.Order.Side == OrderSide.Buy ? addIntents.FirstOrDefault(a => a.Order.CorrelationId == outcome.Order.CorrelationId) : default;
                var pending = pendingExit.Position is not null ? intents.Exit(pendingExit.Position, pendingExit.Reason)
                    : pendingEntry.Candidate is not null ? intents.Entry(pendingEntry.Candidate)
                    : pendingAdd.Position is not null ? intents.Add(pendingAdd.Position, pendingAdd.Plan) : null;
                if (pending is not null)
                    await SaveStateAsync(engineStateRepo, OrderIntent.StateKey(outcome.Order.CorrelationId), pending, cancellationToken);
                continue;
            }

            // Single fill-application path (FillApplier): actual fill price/qty/costs -> Fill row, position, trade, cash.
            var o = outcome.Order;
            var isExit = o.Side == OrderSide.Sell;
            var exitMatch = isExit ? exitIntents.FirstOrDefault(e => e.Order.CorrelationId == o.CorrelationId) : default;
            var entryMatch = isExit ? default : entryIntents.FirstOrDefault(e => e.Order.CorrelationId == o.CorrelationId);
            var addMatch = isExit ? default : addIntents.FirstOrDefault(a => a.Order.CorrelationId == o.CorrelationId);
            OrderIntent? intent = isExit ? (exitMatch.Position is not null ? intents.Exit(exitMatch.Position, exitMatch.Reason) : null)
                : entryMatch.Candidate is not null ? intents.Entry(entryMatch.Candidate)
                : addMatch.Position is not null ? intents.Add(addMatch.Position, addMatch.Plan) : null;
            if (intent is null) continue;
            cash += await FillApplier.ApplyAsync(o, orderId, intent, isExit ? exitMatch.Position : null,
                orderRepo, positionRepo, tradeRepo, signalRepo, riskEventRepo, engineStateRepo, cancellationToken);
        }

        if (drawdownEvent is not null)
            await drawdownEventRepo.InsertAsync(drawdownEvent, cancellationToken);

        if (killSwitchNewlyActivated)
        {
            await riskEventRepo.InsertAsync(new RiskEvent
            {
                EventType = "KillSwitchActivated",
                Description = $"{cycleResult.KillSwitchState.Source}: {cycleResult.KillSwitchState.Reason}",
                Timestamp = cycleResult.KillSwitchState.ActivatedAt ?? DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
            }, cancellationToken);
        }

        await SaveStateAsync(engineStateRepo, DrawdownStateKey, drawdownState, cancellationToken);
        await SaveStateAsync(engineStateRepo, LossLimitStateKey, cycleResult.LossLimitState, cancellationToken);
        await SaveStateAsync(engineStateRepo, KillSwitchStateKey, cycleResult.KillSwitchState, cancellationToken);

        var openPositionsAfter = await positionRepo.GetOpenAsync(cancellationToken);
        // NAV AFTER the cycle: cash (already net of fills and costs) + open positions marked at the last close. The snapshot used
        // to store the pre-cycle NAV next to the post-cycle cash, so the two disagreed and costs never reached NAV/peak/drawdown.
        var navAfterCycle = cash + openPositionsAfter.Sum(p => p.Quantity * markPrices.GetValueOrDefault(p.Symbol, p.AverageEntryPrice));
        await snapshotRepo.UpsertAsync(new PortfolioSnapshot
        {
            AsOfDate = asOfDate, Nav = navAfterCycle, CashBalance = cash, PeakNav = Math.Max(navAfterCycle, drawdownState.PeakNav),
            DrawdownPercent = drawdownState.DrawdownPercent, PortfolioHeatPercent = navAfterCycle > 0 ? heatValueBeforeCycle / navAfterCycle * 100m : 0m,
            OpenPositionCount = openPositionsAfter.Count, Regime = regime,
        }, cancellationToken);
    }

    // ---------------- helpers ----------------

    /// <summary>LiveTradingOptions is a plain mutable class (not a record), so this is a manual field-by-field copy rather than a `with` expression — see the `paper`-always-safe remarks above RunAsync.</summary>
    private static LiveTradingOptions ForcePaperMode(LiveTradingOptions source) => new()
    {
        Mode = TradingMode.Paper,
        AllowLiveTrading = false,
        RejectionConsecutiveThreshold = source.RejectionConsecutiveThreshold,
        AbnormalSlippageThresholdBps = source.AbnormalSlippageThresholdBps,
        AbnormalSlippageConsecutiveThreshold = source.AbnormalSlippageConsecutiveThreshold,
        CashReconciliationToleranceRupees = source.CashReconciliationToleranceRupees,
    };

    /// <summary>
    /// Terminal order outcomes (Rejected/Failed = true, Filled/PartiallyFilled = false), OLDEST FIRST — the shape
    /// RepeatedRejectionDetector expects. Open/cancelled orders are not evidence either way and are skipped. Only
    /// orders created after the last manual kill-switch reset are considered.
    /// </summary>
    /// <summary>
    /// Portfolio-heat cap in RUPEES = NAV x PortfolioHeatMaxPercent. The order validator compares RUPEE heat to this value;
    /// the code previously passed the raw percent (8), so every paper/live entry failed the PortfolioHeat check.
    /// </summary>
    internal static decimal PortfolioHeatCap(decimal nav, RiskOptions risk) => nav * risk.PortfolioHeatMaxPercent / 100m;

    internal static async Task<IReadOnlyList<bool>> BuildRecentRejectionHistoryAsync(IOrderRepository orderRepo, IEngineStateRepository engineStateRepo, CancellationToken cancellationToken)
    {
        var resetJson = await engineStateRepo.GetAsync(KillSwitchResetAtKey, cancellationToken);
        DateTimeOffset? resetAt = resetJson is null ? null : JsonSerializer.Deserialize<DateTimeOffset>(resetJson);
        var recent = await orderRepo.GetRecentAsync(RejectionHistoryWindow, cancellationToken); // newest first
        return recent
            .Where(o => resetAt is null || o.CreatedAt > resetAt.Value)
            .Where(o => o.State is OrderState.Rejected or OrderState.Failed or OrderState.Filled or OrderState.PartiallyFilled)
            .Reverse()
            .Select(o => o.State is OrderState.Rejected or OrderState.Failed)
            .ToList();
    }

    /// <summary>
    /// Realised slippage in basis points of each recent filled order (oldest first), from the Fills table — the
    /// input AbnormalSlippageDetector expects. Slippage is |fill − reference| × qty in rupees, so bps is taken
    /// against the fill's notional. Only orders after the last manual kill-switch reset count.
    /// </summary>
    internal static async Task<IReadOnlyList<decimal>> BuildRecentSlippageHistoryAsync(IOrderRepository orderRepo, IEngineStateRepository engineStateRepo, CancellationToken cancellationToken)
    {
        var resetJson = await engineStateRepo.GetAsync(KillSwitchResetAtKey, cancellationToken);
        DateTimeOffset? resetAt = resetJson is null ? null : JsonSerializer.Deserialize<DateTimeOffset>(resetJson);
        var recent = (await orderRepo.GetRecentAsync(RejectionHistoryWindow, cancellationToken)) // newest first
            .Where(o => o.State is OrderState.Filled or OrderState.PartiallyFilled)
            .Where(o => resetAt is null || o.CreatedAt > resetAt.Value)
            .Reverse().ToList();
        var bps = new List<decimal>();
        foreach (var order in recent)
        {
            var fills = await orderRepo.GetFillsAsync(order.Id, cancellationToken);
            var notional = fills.Sum(f => f.Quantity * f.Price);
            if (notional <= 0) continue;
            bps.Add(fills.Sum(f => f.SlippageAmount) / notional * 10_000m);
        }
        return bps;
    }

    internal static async Task<T?> LoadStateAsync<T>(IEngineStateRepository repo, string key, CancellationToken cancellationToken) where T : class
    {
        var json = await repo.GetAsync(key, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    internal static Task SaveStateAsync<T>(IEngineStateRepository repo, string key, T value, CancellationToken cancellationToken) =>
        repo.SetAsync(key, JsonSerializer.Serialize(value), cancellationToken);

    private static async Task<IReadOnlyList<HistoricalBar>> ResolveBarsAsync(
        string symbol, Dictionary<string, UniverseDataLoader.LoadedSymbol> loadedBySymbol, IHistoricalBarRepository bars,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (loadedBySymbol.TryGetValue(symbol, out var loaded)) return loaded.Bars;
        // An open position in a symbol that fell out of the active-instrument universe (e.g. deactivated) still needs
        // managing — fetch its bars directly rather than silently ignoring the position.
        return await bars.GetRangeAsync(symbol, from, to, Timeframe.Daily, cancellationToken);
    }

    private static IReadOnlyDictionary<string, decimal> ComputeSectorExposure(
        IReadOnlyList<Position> openPositions, IReadOnlyDictionary<string, decimal> currentPrices, Dictionary<string, UniverseDataLoader.LoadedSymbol> loadedBySymbol)
    {
        var result = new Dictionary<string, decimal>();
        foreach (var position in openPositions)
        {
            var sector = loadedBySymbol.TryGetValue(position.Symbol, out var s) ? (s.Classification?.Sector ?? s.Instrument.Sector) : null;
            if (sector is null) continue; // Unknown sector never aggregates against a named sector's cap (spec §2.2)
            var value = position.Quantity * currentPrices.GetValueOrDefault(position.Symbol, position.AverageEntryPrice);
            result[sector] = result.GetValueOrDefault(sector) + value;
        }
        return result;
    }

    private static decimal ComputeBucketExposure(
        IReadOnlyList<Position> openPositions, IReadOnlyDictionary<string, decimal> currentPrices,
        Dictionary<string, UniverseDataLoader.LoadedSymbol> loadedBySymbol, MarketCapBucket bucket) =>
        openPositions
            .Where(p => loadedBySymbol.TryGetValue(p.Symbol, out var s) && s.MarketCapBucket == bucket)
            .Sum(p => p.Quantity * currentPrices.GetValueOrDefault(p.Symbol, p.AverageEntryPrice));

    private static IExecutionProvider BuildPaperExecutionProvider(IQuoteSource quotes, BacktestOptions backtestOptions, RiskOptions riskOptions) =>
        new PaperExecutionProvider(quotes, backtestOptions, riskOptions);

    /// <summary>Null on any failure (missing credentials, broker error) — the caller falls back to Paper rather than propagating, since Live execution failing must never crash the whole cycle (spec §6.4's connectivity-failure kill-switch source exists precisely because broker unavailability is an expected, not exceptional, condition).</summary>
    private static IExecutionProvider? TryBuildLiveExecutionProvider(IServiceProvider provider, ZerodhaOptions zerodhaOptions, ILogger logger)
    {
        var apiKey = Environment.GetEnvironmentVariable(zerodhaOptions.ApiKeyEnvVar);
        var accessToken = Environment.GetEnvironmentVariable(zerodhaOptions.AccessTokenEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(accessToken))
        {
            logger.LogWarning("trade-cycle: Live mode requested but {ApiKeyVar}/{AccessTokenVar} are not both set.", zerodhaOptions.ApiKeyEnvVar, zerodhaOptions.AccessTokenEnvVar);
            return null;
        }

        try
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var transport = new KiteHttpTransport(httpClientFactory, zerodhaOptions.RequestTimeoutSeconds);
            var gateway = new KiteApiGateway(transport, zerodhaOptions, apiKey, accessToken);
            return new ZerodhaOrderExecutionService(gateway);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "trade-cycle: failed to construct the live Zerodha execution provider.");
            return null;
        }
    }

    private static async Task<OrderSyncResult?> TrySyncLiveOrdersAsync(IServiceProvider provider, ZerodhaOptions zerodhaOptions,
        IOrderRepository orderRepo, IPositionRepository positionRepo, ITradeRepository tradeRepo, ISignalRepository signalRepo,
        IRiskEventRepository riskEventRepo, IEngineStateRepository engineStateRepo, INotificationService? notifier, ILogger logger, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(zerodhaOptions.ApiKeyEnvVar);
        var accessToken = Environment.GetEnvironmentVariable(zerodhaOptions.AccessTokenEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(accessToken)) return null; // the broker-state gate below halts the cycle
        try
        {
            var transport = new KiteHttpTransport(provider.GetRequiredService<IHttpClientFactory>(), zerodhaOptions.RequestTimeoutSeconds);
            var service = new ZerodhaOrderExecutionService(new KiteApiGateway(transport, zerodhaOptions, apiKey, accessToken));
            var result = await LiveOrderSync.RunAsync(service.GetAllOrdersAsync, orderRepo, positionRepo, tradeRepo, signalRepo, riskEventRepo, engineStateRepo, notifier, logger, cancellationToken);
            logger.LogInformation("order-sync: checked={Checked} filled={Filled} terminal={Terminal} missingAtBroker={Missing} cashDelta={Cash}.", result.Checked, result.Filled, result.Terminal, result.MissingAtBroker, result.CashDelta);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "order-sync failed — pending orders stay pending; reconciliation will flag any drift.");
            return null;
        }
    }

    private static async Task<(IReadOnlyList<Position> Positions, decimal Cash)?> TryFetchBrokerStateAsync(IServiceProvider provider, ZerodhaOptions zerodhaOptions, ILogger logger, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(zerodhaOptions.ApiKeyEnvVar);
        var accessToken = Environment.GetEnvironmentVariable(zerodhaOptions.AccessTokenEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(accessToken)) return null;

        try
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var transport = new KiteHttpTransport(httpClientFactory, zerodhaOptions.RequestTimeoutSeconds);
            var gateway = new KiteApiGateway(transport, zerodhaOptions, apiKey, accessToken);
            var positionService = new ZerodhaPositionService(gateway);
            var accountService = new ZerodhaAccountService(gateway);
            var positions = await positionService.GetBrokerPositionsAsync(cancellationToken);
            var cash = await accountService.GetAvailableCashAsync(cancellationToken);
            return (positions, cash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "trade-cycle: broker state fetch failed — reconciliation for this cycle will compare local state against itself only.");
            return null;
        }
    }

    /// <summary>Latest-cached-bar-as-quote (see class remarks: this is a periodic daily cycle, not a live quote stream) — reuses whatever UniverseDataLoader already loaded rather than re-querying SQLite per order.</summary>
    private sealed class CachedBarQuoteSource : IQuoteSource
    {
        private readonly Dictionary<string, UniverseDataLoader.LoadedSymbol> _loadedBySymbol;

        public CachedBarQuoteSource(Dictionary<string, UniverseDataLoader.LoadedSymbol> loadedBySymbol) => _loadedBySymbol = loadedBySymbol;

        public Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
        {
            if (!_loadedBySymbol.TryGetValue(symbol, out var loaded) || loaded.Bars.Count == 0)
                throw new InvalidOperationException($"No cached bars available to quote {symbol}.");

            var lookback = Math.Min(20, loaded.Bars.Count);
            var recent = loaded.Bars.Skip(loaded.Bars.Count - lookback).ToList();
            var adtv = recent.Average(b => b.Close * b.Volume);

            return Task.FromResult(new PaperQuote(loaded.Bars[^1], adtv, loaded.MarketCapBucket));
        }
    }
}
