using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators;
using TradingPlatform.Risk;
using TradingPlatform.Risk.Correlation;
using TradingPlatform.Risk.Sizing;
using TradingPlatform.Scanner.Universe;
using TradingPlatform.Strategy.Breakout;
using TradingPlatform.Strategy.RelativeStrength;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Strategy.Structure;
using TradingPlatform.Strategy.Scoring;
using TradingPlatform.Strategy.Vcp;

namespace TradingPlatform.Scanner;

/// <summary>
/// Implements spec §7.1's scanner pipeline: "Universe → Liquidity → Regime
/// → Indicators → RS → Volume → VCP → Fundamental quality → Risk filter →
/// Score → Rank." Runs once per scan cycle over the whole candidate
/// universe, producing the top-N ranked ScannerResult rows spec's own
/// column list already fully specifies (Tranche 0).
///
/// Universe and Liquidity are hard, exclusionary filters — a candidate
/// that fails either is dropped before any further computation, matching
/// how spec presents them as the pipeline's first two gates rather than
/// scoring inputs. "Fundamental quality" is folded into the scoring step
/// itself (StockScoringEngine's FundamentalScore/EarningsAccelerationScore/
/// RevenueAccelerationScore components, Tranche 6) rather than a separate
/// gate — spec lists it immediately before scoring, and StockScoringEngine
/// already IS the fundamental-quality evaluation this platform has.
/// "Risk filter" is the surveillance/risk-flag check RE-APPLIED at scan
/// time (flags can change day to day, unlike Universe eligibility's
/// mostly-static fields) — a second, exclusionary pass, not a scoring
/// input, since a stock actively under surveillance shouldn't be a fresh
/// signal candidate regardless of how well it scores otherwise.
///
/// Portfolio-level sizing rejections (position/sector/microcap/heat caps,
/// insufficient cash — see PositionSizingCalculator, Tranche 8) are
/// handled differently: they stay in the ranked list with
/// SuggestedPositionSize = 0 and the rejection folded into BreakoutStatus,
/// rather than being excluded outright. That's a deliberate distinction:
/// those checks answer "can I afford this right now," not "is this stock
/// investment-worthy" — a trader watching the scanner benefits from
/// seeing a well-scored candidate that's currently capital-constrained,
/// not having it silently vanish.
/// </summary>
/// <summary>Why a symbol was dropped before ranking (diagnostics only — never changes what is ranked).</summary>
public sealed record ScanRejection(string Symbol, string Gate, string Detail);

/// <summary>A scan's ranked candidates together with the regime snapshot they were ranked under (so the caller can persist/print the exact regime used).</summary>
public sealed record ScanOutcome(IReadOnlyList<ScannerResult> Results, MarketRegimeSnapshot Regime, IReadOnlyList<ScanEvidence> Evidence);

public static class ScannerEngine
{
    public static IReadOnlyList<ScannerResult> Scan(
        DateOnly asOfDate,
        IReadOnlyList<HistoricalBar> broadIndexBars,
        IReadOnlyList<HistoricalBar>? midcapIndexBars,
        IReadOnlyList<ScannerSymbolContext> symbols,
        ScannerPortfolioContext? portfolioContext,
        ScannerEngineOptions options,
        IList<ScanRejection>? rejections = null)
        => ScanDetailed(asOfDate, broadIndexBars, midcapIndexBars, symbols, portfolioContext, options, rejections).Results;

    public static ScanOutcome ScanDetailed(
        DateOnly asOfDate,
        IReadOnlyList<HistoricalBar> broadIndexBars,
        IReadOnlyList<HistoricalBar>? midcapIndexBars,
        IReadOnlyList<ScannerSymbolContext> symbols,
        ScannerPortfolioContext? portfolioContext,
        ScannerEngineOptions options,
        IList<ScanRejection>? rejections = null)
    {
        // Spec §3.1 universe-wide inputs, from the same cached bars the scan ranks (the broad index's last session is the reference date,
        // so a stale/halted symbol is not counted as today's market state).
        UniverseBreadth breadth = new(null, null, null, 0, 0, 0);
        if (options.PrecomputedBreadth is { } pre) breadth = pre;
        else if (broadIndexBars.Count > 0)
        {
            var referenceDate = DateOnly.FromDateTime(broadIndexBars[^1].Timestamp.Date);
            var observations = new List<BreadthObservation>(symbols.Count);
            foreach (var s in symbols)
                if (BreadthObservation.FromBars(s.Bars, referenceDate) is { } o) observations.Add(o);
            breadth = UniverseBreadthCalculator.Calculate(observations, options.MarketRegime.BreadthMinimumSampleSize);
        }
        var breakoutSuccess = options.RecentClosedTrades is { Count: > 0 } closed
            ? BreakoutSuccessRateCalculator.Calculate(closed, asOfDate, options.MarketRegime)
            : null;

        var regimeSnapshot = MarketRegimeEngine.Calculate(
            new MarketRegimeInputs
            {
                BroadIndexBars = broadIndexBars, MidcapIndexBars = midcapIndexBars,
                PercentUniverseAbove50Dma = breadth.PercentAbove50Dma, PercentUniverseAbove200Dma = breadth.PercentAbove200Dma,
                AdvanceDeclineRatio = breadth.AdvanceDeclineRatio, BreakoutSuccessRatePercent = breakoutSuccess,
            },
            options.MarketRegime, options.Rs, asOfDate);

        var sectorStrengthCache = new Dictionary<string, decimal>();
        var scanTimestamp = DateTimeOffset.UtcNow;
        var results = new List<ScannerResult>();
        var evidenceBySymbol = new Dictionary<string, ScanEvidence>();

        foreach (var ctx in symbols)
        {
            if (ctx.Bars.Count == 0) { rejections?.Add(new(ctx.Instrument.Symbol, "NoBars", "No cached bars for this symbol — run fetch.")); continue; }
            var lastBar = ctx.Bars[^1];

            var eligibility = UniverseEligibilityFilter.Evaluate(ctx.Instrument, lastBar.Close, ctx.Bars.Count, ctx.Surveillance, options.Scanner);
            if (!eligibility.IsEligible)
            {
                foreach (var failed in eligibility.Checks.Where(c => !c.Passed)) rejections?.Add(new(ctx.Instrument.Symbol, failed.Name, failed.Detail));
                continue; // Universe filter
            }

            var liquidity = LiquidityMetricsCalculator.Calculate(ctx.Bars, options.Scanner.LiquidityLookbackDays);
            if (liquidity.AverageDailyTradedValue < options.Scanner.MinimumAverageDailyTurnover)
            {
                rejections?.Add(new(ctx.Instrument.Symbol, "LiquidityFromBars", $"Average traded value over {options.Scanner.LiquidityLookbackDays}d is {liquidity.AverageDailyTradedValue:F0} vs minimum {options.Scanner.MinimumAverageDailyTurnover:F0} (computed from the cached bars, not the Instruments column)."));
                continue; // Liquidity filter
            }

            var hasCriticalSurveillanceFlag = ctx.Surveillance.Availability != DataAvailability.Available || ctx.Surveillance.Flags != RiskFlags.None;
            if (hasCriticalSurveillanceFlag) continue; // Risk filter — re-applied at scan time

            var indicatorSeries = IndicatorEngine.ComputeSeries(ctx.Bars, options.Indicator);
            var lastIndicators = indicatorSeries[^1];

            var vcp = VcpDetector.Detect(ctx.Bars, options.Vcp);

            var benchmarks = new Dictionary<string, IReadOnlyList<HistoricalBar>> { [BenchmarkNames.BroadIndex] = broadIndexBars };
            if (midcapIndexBars is { Count: > 0 }) benchmarks[BenchmarkNames.Midcap] = midcapIndexBars;
            if (ctx.SectorBenchmarkBars is { Count: > 0 }) benchmarks[BenchmarkNames.Sector] = ctx.SectorBenchmarkBars;
            var rs = RelativeStrengthEngine.Calculate(ctx.Bars, benchmarks, options.Rs);

            var bucket = ctx.Classification?.MarketCapBucket ?? MarketCapBucket.Unknown;
            var sector = ctx.Classification?.Sector ?? ctx.Instrument.Sector;
            var sectorStrength = ComputeSectorStrengthScore(sector, ctx.SectorBenchmarkBars, broadIndexBars, options.Rs, sectorStrengthCache);

            // Delivery % (spec §3.4): evaluated for the last bar's session only; nothing is carried forward from earlier days.
            var deliverySnapshot = ctx.DeliveryRecords is { Count: > 0 } dr
                ? TradingPlatform.Strategy.Scoring.DeliveryAnalyzer.Evaluate(dr.OrderBy(r => r.TradeDate).ToList(), ctx.DeliveryAsOfDate ?? DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date), options.StockScoring)
                : null;
            var stockScore = StockScoringEngine.Calculate(new StockScoringInputs
            {
                Symbol = ctx.Instrument.Symbol,
                AsOfDate = asOfDate,
                Bars = ctx.Bars,
                IndicatorSnapshots = indicatorSeries,
                VcpPattern = vcp,
                RelativeStrengthScore = rs.BlendedScore,
                SectorStrengthScore = sectorStrength,
                CurrentFundamentals = ctx.Fundamentals,
                PriorFundamentals = ctx.PriorFundamentals,
                InstitutionalActivity = ctx.InstitutionalActivity,
                Delivery = deliverySnapshot,
            }, options.Strategy, options.StockScoring);

            var pivotPrice = 0m;
            var distanceFromPivotPercent = 0m;
            var breakoutStatus = "No VCP pattern";
            if (vcp is not null)
            {
                pivotPrice = vcp.PivotPrice;
                distanceFromPivotPercent = pivotPrice > 0 ? (lastBar.Close - pivotPrice) / pivotPrice * 100m : 0m;
                var breakoutEval = BreakoutEngine.Evaluate(lastBar.Close, pivotPrice, lastIndicators, rs.BlendedScore, regimeSnapshot.Classification, options.Strategy, options.Rs, deliverySnapshot);
                breakoutStatus = breakoutEval.IsBreakout ? "Breakout confirmed"
                    : lastBar.Close >= pivotPrice ? "At/above pivot, unconfirmed"
                    : "Below pivot";
            }

            var suggestedEntry = pivotPrice > 0 ? pivotPrice : lastBar.Close;
            var suggestedStop = 0m;
            var risk = 0m;
            var suggestedQty = 0m;

            if (portfolioContext is not null)
            {
                suggestedStop = InitialStopCalculator.Calculate(ctx.Bars, ctx.Bars.Count - 1, suggestedEntry, options.Risk);
                if (suggestedStop < suggestedEntry)
                {
                    var correlatedExposure = CorrelationClusterCalculator.CalculateClusterExposure(ctx.Bars, portfolioContext.ExistingOpenPositionsForCorrelation, options.Risk);
                    var sectorExposure = sector is not null && portfolioContext.CurrentSectorExposureValueBySector.TryGetValue(sector, out var se) ? se : 0m;
                    var deployablePercent = Math.Min(regimeSnapshot.DeployableCapitalPercent, portfolioContext.DeployableCapitalPercentFromDrawdownAxis);

                    var sizing = PositionSizingCalculator.Calculate(new PositionSizingInputs
                    {
                        Symbol = ctx.Instrument.Symbol,
                        EntryPrice = suggestedEntry,
                        StopPrice = suggestedStop,
                        Nav = portfolioContext.Nav,
                        CashBalance = portfolioContext.CashBalance,
                        AverageDailyTradedValue = liquidity.AverageDailyTradedValue,
                        StockScoreTotalScore = stockScore.TotalScore,
                        MarketCapBucket = bucket,
                        CurrentSectorExposureValue = sectorExposure,
                        CurrentMicrocapExposureValue = portfolioContext.CurrentMicrocapExposureValue,
                        CurrentPortfolioHeatValue = portfolioContext.CurrentPortfolioHeatValue,
                        CorrelatedClusterExposureValue = correlatedExposure,
                        DeployableCapitalPercent = deployablePercent,
                        TechnicalScore = stockScore.TechnicalScore,
                        FundamentalScore = stockScore.FundamentalScore,
                        HasCriticalSurveillanceFlag = false, // already excluded above if true
                        HasSevereDataQualityIssue = stockScore.DataQualityScore < options.Scanner.SevereDataQualityScoreThreshold,
                    }, options.Risk, options.Portfolio);

                    if (sizing.Rejected)
                    {
                        breakoutStatus += $" (sizing blocked: {sizing.RejectionReason})";
                    }
                    else
                    {
                        suggestedQty = sizing.FinalQty;
                        risk = suggestedQty * (suggestedEntry - suggestedStop);
                    }
                }
            }

            evidenceBySymbol[ctx.Instrument.Symbol] = new ScanEvidence(ctx.Instrument.Symbol, stockScore, vcp, lastIndicators,
                PriceStructureAnalyzer.Analyze(ctx.Bars, ctx.Bars[^1].Close, options.Vcp.SwingLookback));
            results.Add(new ScannerResult
            {
                ScanTimestamp = scanTimestamp,
                Symbol = ctx.Instrument.Symbol,
                Price = lastBar.Close,
                MarketCap = ctx.Instrument.MarketCap,
                Sector = sector,
                Score = stockScore.TotalScore,
                RelativeStrengthScore = rs.BlendedScore,
                MomentumScore = stockScore.PriceMomentumScore,
                VolumeScore = stockScore.VolumeScore,
                VcpScore = stockScore.VcpScore,
                Adx = lastIndicators.Adx,
                Rsi = lastIndicators.Rsi,
                Atr = lastIndicators.Atr,
                PivotPrice = pivotPrice,
                BreakoutStatus = breakoutStatus,
                DistanceFromPivotPercent = distanceFromPivotPercent,
                SuggestedEntry = suggestedEntry,
                SuggestedStop = suggestedStop,
                Risk = risk,
                SuggestedPositionSize = suggestedQty,
            });
        }

        var ranked = results.OrderByDescending(r => r.Score).Take(options.Scanner.TopN).ToList();
        for (var i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        var evidence = ranked.Where(r => evidenceBySymbol.ContainsKey(r.Symbol)).Select(r => evidenceBySymbol[r.Symbol]).ToList();
        return new ScanOutcome(ranked, regimeSnapshot, evidence);
    }

    /// <summary>Sector-index-vs-broad-index RS — identical for every candidate in the same sector on a given scan (see StockScoringInputs' XML doc, Tranche 6/11) — computed once per sector per scan via the cache.</summary>
    private static decimal? ComputeSectorStrengthScore(
        string? sector, IReadOnlyList<HistoricalBar>? sectorBenchmarkBars, IReadOnlyList<HistoricalBar> broadIndexBars,
        TradingPlatform.Core.Configuration.RsOptions rsOptions, Dictionary<string, decimal> cache)
    {
        if (sector is null || sectorBenchmarkBars is not { Count: > 0 }) return null;
        if (cache.TryGetValue(sector, out var cached)) return cached;

        var (sectorCloses, broadCloses) = PriceSeriesAligner.AlignByDate(sectorBenchmarkBars, broadIndexBars);
        if (sectorCloses.Count == 0) return null;

        var score = RelativeStrengthCalculator.Calculate(sectorCloses, broadCloses, rsOptions).CompositeScore;
        cache[sector] = score;
        return score;
    }
}
