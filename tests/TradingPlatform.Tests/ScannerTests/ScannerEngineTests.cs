using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Risk.Correlation;
using TradingPlatform.Scanner;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ScannerTests;

/// <summary>
/// End-to-end proof that ScannerEngine correctly wires Universe/Liquidity
/// filtering, indicators, VCP, RS, regime, scoring, breakout evaluation,
/// and sizing together — reuses BacktestEngineFixture's already-verified
/// breakout scenario (Tranche 11) rather than re-engineering a fresh
/// fixture, since it's the same underlying question ("does a real VCP
/// breakout get recognized correctly") from a different angle (a
/// point-in-time scan instead of a day-by-day replay).
/// </summary>
public class ScannerEngineTests
{
    private static ScannerEngineOptions BuildOptions() => new()
    {
        Scanner = new ScannerOptions { MinimumHistoricalBarsRequired = 30, MinimumAverageDailyTurnover = 1m, MinimumPrice = 1m, TopN = 30 },
        Indicator = new IndicatorOptions(),
        Vcp = new VcpOptions(),
        Rs = new RsOptions(),
        StockScoring = new StockScoringOptions(),
        Strategy = new StrategyOptions(),
        MarketRegime = new MarketRegimeOptions(),
        Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
        Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m },
    };

    private static Instrument MakeInstrument(string symbol) => new()
    {
        Symbol = symbol, NseSymbol = symbol, YahooSymbol = $"{symbol}.NS", CompanyName = $"{symbol} Ltd",
        Sector = "IT", Exchange = "NSE", Active = true, AverageDailyTurnover = 5_00_00_000m, MarketCap = 20_000m,
    };

    private static SurveillanceStatus ClearSurveillance(string symbol) => new()
    { Symbol = symbol, AsOfDate = new DateOnly(2026, 3, 1), Flags = RiskFlags.None, Availability = DataAvailability.Available };

    [Fact]
    public void Scan_RecognizesTheBreakoutScenario_AsATopCandidate()
    {
        // Day 51 of BacktestEngineFixture's scenario is where the breakout scenario first
        // clears every BreakoutEngine condition (confirmed during Tranche 11's fixture work).
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        var symbolContext = new ScannerSymbolContext
        {
            Instrument = MakeInstrument(BacktestEngineFixture.Symbol),
            Bars = stockBars,
            Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol),
            Classification = new UniverseClassificationSnapshot { Symbol = BacktestEngineFixture.Symbol, MarketCapBucket = MarketCapBucket.Mid, EffectiveFrom = new DateOnly(2020, 1, 1) },
        };

        var results = ScannerEngine.Scan(
            DateOnly.FromDateTime(stockBars[^1].Timestamp.Date),
            benchmarkBars, null,
            new[] { symbolContext }, null, BuildOptions());

        Assert.Equal(1, results.Count);
        Assert.Equal(BacktestEngineFixture.Symbol, results[0].Symbol);
        Assert.Equal("Breakout confirmed", results[0].BreakoutStatus);
        Assert.Equal(1, results[0].Rank);
    }

    private static ScannerSymbolContext BreakoutContext(out IReadOnlyList<HistoricalBar> benchmark)
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        benchmark = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);
        return new ScannerSymbolContext
        {
            Instrument = MakeInstrument(BacktestEngineFixture.Symbol), Bars = stockBars, Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol),
            Classification = new UniverseClassificationSnapshot { Symbol = BacktestEngineFixture.Symbol, MarketCapBucket = MarketCapBucket.Mid, EffectiveFrom = new DateOnly(2020, 1, 1) },
        };
    }

    [Fact]
    public void ScanDetailed_ReturnsTheRegimeUsed_AndEvidenceForEveryRankedSymbol()
    {
        var ctx = BreakoutContext(out var benchmark);
        var asOf = DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date);

        var outcome = ScannerEngine.ScanDetailed(asOf, benchmark, null, new[] { ctx }, null, BuildOptions());

        Assert.Equal(1, outcome.Results.Count);
        Assert.Equal(asOf, outcome.Regime.AsOfDate);
        var evidence = Assert.Single(outcome.Evidence);
        Assert.Equal(BacktestEngineFixture.Symbol, evidence.Symbol);
        Assert.Equal(outcome.Results[0].Score, evidence.Score.TotalScore);
        Assert.True(evidence.Vcp is not null, "the breakout scenario has a VCP pattern, which must be carried through");
        Assert.Equal(outcome.Results[0].PivotPrice, evidence.Vcp!.PivotPrice);
        Assert.Equal(outcome.Results[0].Adx, evidence.Indicators.Adx);
    }

    [Fact]
    public void ScanDetailed_BreadthFromAShortlistOfOne_IsUnavailable_NotComputedFromOneStock()
    {
        var ctx = BreakoutContext(out var benchmark);
        var asOf = DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date);

        var outcome = ScannerEngine.ScanDetailed(asOf, benchmark, null, new[] { ctx }, null, BuildOptions());

        Assert.Null(outcome.Regime.PercentUniverseAbove50Dma);
        Assert.Null(outcome.Regime.AdvanceDeclineRatio);
    }

    [Fact]
    public void ScanDetailed_PrecomputedBreadth_OverridesWhatTheShortlistWouldGive()
    {
        var ctx = BreakoutContext(out var benchmark);
        var asOf = DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date);
        var options = BuildOptions().WithPrecomputedBreadth(new TradingPlatform.Strategy.Regime.UniverseBreadth(72m, 61m, 1.9m, 120, 63, 300));

        var outcome = ScannerEngine.ScanDetailed(asOf, benchmark, null, new[] { ctx }, null, options);

        Assert.Equal(72m, outcome.Regime.PercentUniverseAbove50Dma!.Value);
        Assert.Equal(61m, outcome.Regime.PercentUniverseAbove200Dma!.Value);
        Assert.Equal(1.9m, outcome.Regime.AdvanceDeclineRatio!.Value);
    }

    [Fact]
    public void Scan_StillReturnsJustTheResults_SoExistingCallersAreUnchanged()
    {
        var ctx = BreakoutContext(out var benchmark);
        var asOf = DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date);

        var plain = ScannerEngine.Scan(asOf, benchmark, null, new[] { ctx }, null, BuildOptions());
        var detailed = ScannerEngine.ScanDetailed(asOf, benchmark, null, new[] { ctx }, null, BuildOptions());

        Assert.Equal(detailed.Results.Count, plain.Count);
        Assert.Equal(detailed.Results[0].Score, plain[0].Score);
    }

    [Fact]
    public void Scan_ExcludesInstrumentThatFailsEligibility()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        var instrument = MakeInstrument(BacktestEngineFixture.Symbol);
        instrument.Active = false; // fails Universe eligibility

        var symbolContext = new ScannerSymbolContext { Instrument = instrument, Bars = stockBars, Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol) };

        var results = ScannerEngine.Scan(DateOnly.FromDateTime(stockBars[^1].Timestamp.Date), benchmarkBars, null, new[] { symbolContext }, null, BuildOptions());

        Assert.Equal(0, results.Count);
    }

    [Fact]
    public void Scan_ExcludesInstrumentWithSurveillanceFlag()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        var flagged = new SurveillanceStatus { Symbol = BacktestEngineFixture.Symbol, AsOfDate = new DateOnly(2026, 3, 1), Flags = RiskFlags.Gsm, Availability = DataAvailability.Available };
        var symbolContext = new ScannerSymbolContext { Instrument = MakeInstrument(BacktestEngineFixture.Symbol), Bars = stockBars, Surveillance = flagged };

        var results = ScannerEngine.Scan(DateOnly.FromDateTime(stockBars[^1].Timestamp.Date), benchmarkBars, null, new[] { symbolContext }, null, BuildOptions());

        Assert.Equal(0, results.Count);
    }

    [Fact]
    public void Scan_WithPortfolioContext_ComputesRealSuggestedSizing()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        var symbolContext = new ScannerSymbolContext { Instrument = MakeInstrument(BacktestEngineFixture.Symbol), Bars = stockBars, Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol) };

        var portfolioContext = new ScannerPortfolioContext
        {
            Nav = 1_000_000m,
            CashBalance = 1_000_000m,
            DeployableCapitalPercentFromDrawdownAxis = 100m,
            CurrentPortfolioHeatValue = 0m,
            CurrentMicrocapExposureValue = 0m,
            CurrentSectorExposureValueBySector = new Dictionary<string, decimal>(),
            ExistingOpenPositionsForCorrelation = Array.Empty<CorrelatedPosition>(),
        };

        var results = ScannerEngine.Scan(DateOnly.FromDateTime(stockBars[^1].Timestamp.Date), benchmarkBars, null, new[] { symbolContext }, portfolioContext, BuildOptions());

        Assert.Equal(1, results.Count);
        Assert.True(results[0].SuggestedPositionSize > 0m);
        Assert.True(results[0].SuggestedStop > 0m);
        Assert.True(results[0].SuggestedStop < results[0].SuggestedEntry);
        Assert.True(results[0].Risk > 0m);
    }

    [Fact]
    public void Scan_WithoutPortfolioContext_LeavesSizingColumnsAtZero_NotFabricated()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);
        var symbolContext = new ScannerSymbolContext { Instrument = MakeInstrument(BacktestEngineFixture.Symbol), Bars = stockBars, Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol) };

        var results = ScannerEngine.Scan(DateOnly.FromDateTime(stockBars[^1].Timestamp.Date), benchmarkBars, null, new[] { symbolContext }, null, BuildOptions());

        Assert.Equal(0m, results[0].SuggestedPositionSize);
        Assert.Equal(0m, results[0].SuggestedStop);
    }

    [Fact]
    public void Scan_RanksMultipleCandidates_TopNRespected()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(52).ToList();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        var strongContext = new ScannerSymbolContext { Instrument = MakeInstrument("STRONG"), Bars = RetagSymbol(stockBars, "STRONG"), Surveillance = ClearSurveillance("STRONG") };

        // A flat, unremarkable series -- should score far lower than the breakout scenario.
        var flatBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);
        var weakContext = new ScannerSymbolContext { Instrument = MakeInstrument("WEAK"), Bars = RetagSymbol(flatBars, "WEAK"), Surveillance = ClearSurveillance("WEAK") };

        var options = BuildOptions();
        options.Scanner.TopN = 1;

        var results = ScannerEngine.Scan(DateOnly.FromDateTime(stockBars[^1].Timestamp.Date), benchmarkBars, null, new[] { weakContext, strongContext }, null, options);

        Assert.Equal(1, results.Count); // TopN=1 respected even with 2 eligible candidates
        Assert.Equal("STRONG", results[0].Symbol);
    }

    private static IReadOnlyList<HistoricalBar> RetagSymbol(IReadOnlyList<HistoricalBar> bars, string symbol) =>
        bars.Select(b => new HistoricalBar
        {
            Symbol = symbol, Timestamp = b.Timestamp, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
            AdjustedClose = b.AdjustedClose, Volume = b.Volume, Source = b.Source, Timeframe = b.Timeframe,
        }).ToList();

    // ---------------- delivery % gate (spec §3.4) ----------------

    private static ScannerSymbolContext DeliveryContext(int barCount, IReadOnlyList<DeliveryRecord>? records, DateOnly? asOf = null)
    {
        var stockBars = BacktestEngineFixture.BuildStockBars().Take(barCount).ToList();
        return new ScannerSymbolContext
        {
            Instrument = MakeInstrument(BacktestEngineFixture.Symbol), Bars = stockBars, Surveillance = ClearSurveillance(BacktestEngineFixture.Symbol),
            Classification = new UniverseClassificationSnapshot { Symbol = BacktestEngineFixture.Symbol, MarketCapBucket = MarketCapBucket.Mid, EffectiveFrom = new DateOnly(2020, 1, 1) },
            DeliveryRecords = records, DeliveryAsOfDate = asOf,
        };
    }

    private static string Status(ScannerSymbolContext ctx, ScannerEngineOptions options)
    {
        var bench = BacktestEngineFixture.BuildFlatBenchmark(ctx.Bars.Count);
        var asOf = DateOnly.FromDateTime(ctx.Bars[^1].Timestamp.Date);
        return ScannerEngine.Scan(asOf, bench, null, new[] { ctx }, null, options).SingleOrDefault()?.BreakoutStatus ?? "(not ranked)";
    }

    /// <summary>Daily records: 20% for 25 sessions before <paramref name="lastDate"/>, then <paramref name="lastPercent"/> on lastDate itself.</summary>
    private static List<DeliveryRecord> Records(DateOnly lastDate, decimal lastPercent) =>
        Enumerable.Range(1, 25).Select(i => new DeliveryRecord { Symbol = BacktestEngineFixture.Symbol, TradeDate = lastDate.AddDays(-i), DeliveryPercent = 20m })
            .Append(new DeliveryRecord { Symbol = BacktestEngineFixture.Symbol, TradeDate = lastDate, DeliveryPercent = lastPercent }).ToList();

    [Fact]
    public void DeliveryGate_FailsClosedWithoutData_PassesOnHighDelivery_AndFailsOnLow()
    {
        // Find a bar count at which the plain scan reports a confirmed breakout (the golden series breaks out part-way through).
        var barCount = Enumerable.Range(40, 80).FirstOrDefault(n => Status(DeliveryContext(n, null), BuildOptions()) == "Breakout confirmed");
        Assert.True(barCount > 0, "fixture should contain a confirmed breakout day");
        var last = DateOnly.FromDateTime(BacktestEngineFixture.BuildStockBars()[barCount - 1].Timestamp.Date);
        var gated = BuildOptions(); gated.Strategy.BreakoutMinDeliveryRatio = 1.2m;

        Assert.Equal("Breakout confirmed", Status(DeliveryContext(barCount, null), BuildOptions()));                     // control: gate off
        Assert.NotEqual("Breakout confirmed", Status(DeliveryContext(barCount, null), gated));                           // gate on, no data -> blocked
        Assert.Equal("Breakout confirmed", Status(DeliveryContext(barCount, Records(last, 60m)), gated));                // 60 / 20 = 3.0x
        Assert.NotEqual("Breakout confirmed", Status(DeliveryContext(barCount, Records(last, 20m)), gated));             // 1.0x < 1.2x
    }

    [Fact]
    public void DeliveryAsOfDate_LetsAnOverlayScanUseTheLastCompletedSession()
    {
        var barCount = Enumerable.Range(40, 80).First(n => Status(DeliveryContext(n, null), BuildOptions()) == "Breakout confirmed");
        var last = DateOnly.FromDateTime(BacktestEngineFixture.BuildStockBars()[barCount - 1].Timestamp.Date);
        var gated = BuildOptions(); gated.Strategy.BreakoutMinDeliveryRatio = 1.2m;
        // Delivery exists up to YESTERDAY only (published after the close), as it would while today's session is still forming.
        var yesterday = last.AddDays(-1);
        var records = Records(yesterday, 60m);

        Assert.NotEqual("Breakout confirmed", Status(DeliveryContext(barCount, records), gated));                         // today's figure does not exist yet
        Assert.Equal("Breakout confirmed", Status(DeliveryContext(barCount, records, asOf: yesterday), gated));           // use the last completed session
    }
}
