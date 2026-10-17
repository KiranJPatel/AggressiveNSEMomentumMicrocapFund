using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;
using TradingPlatform.Risk.Sizing;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>Entries are sized from the pivot but fill at the open, which is often higher: the gap-risk guard keeps rupee risk at the planned budget.</summary>
public class GapRiskTests
{
    // ---------------- the pure adjuster (hand-computed) ----------------

    [Fact]
    public void FillAtOrBelowThePlannedEntry_IsLeftAlone_NeverScaledUp()
    {
        // planned entry 100, stop 90 (risk 10/share). Filling at 100 or even 95 carries <= planned risk: quantity must not grow.
        var at = GapRiskAdjuster.Adjust(100m, 200m, plannedEntry: 100m, stop: 90m, expectedFillPrice: 100m);
        var below = GapRiskAdjuster.Adjust(100m, 200m, 100m, 90m, 95m);

        Assert.False(at.Resized); Assert.Equal(100m, at.Stage1Quantity); Assert.Equal(200m, at.TotalTargetQuantity);
        Assert.False(below.Resized); Assert.Equal(100m, below.Stage1Quantity); Assert.Equal(5m, below.ActualRiskPerShare);
    }

    [Fact]
    public void FillAboveThePlannedEntry_ScalesQuantityByPlannedOverActualRiskPerShare()
    {
        // planned risk/share 10 (100-90); fill 110 => actual 20/share => factor 0.5.
        var r = GapRiskAdjuster.Adjust(100m, 200m, 100m, 90m, 110m);

        Assert.True(r.Resized); Assert.False(r.Cancelled);
        Assert.Equal(50m, r.Stage1Quantity);
        Assert.Equal(100m, r.TotalTargetQuantity);
        Assert.Equal(20m, r.ActualRiskPerShare);
        // rupee risk of the whole position: planned 200 x 10 = 2000; scaled 100 x 20 = 2000
        Assert.Equal(200m * 10m, r.TotalTargetQuantity * r.ActualRiskPerShare);
    }

    [Fact]
    public void ScaledQuantities_FloorToWholeShares_NeverRoundUpIntoMoreRisk()
    {
        // factor = 10/13 = 0.769...; 7 x 0.769 = 5.38 -> 5; 15 x 0.769 = 11.5 -> 11
        var r = GapRiskAdjuster.Adjust(7m, 15m, 100m, 90m, 103m);

        Assert.Equal(5m, r.Stage1Quantity);
        Assert.Equal(11m, r.TotalTargetQuantity);
        Assert.True(r.TotalTargetQuantity * r.ActualRiskPerShare <= 15m * 10m, "rounding must never push risk above the budget");
    }

    [Fact]
    public void FillAtOrThroughTheStop_CancelsTheEntry()
    {
        Assert.True(GapRiskAdjuster.Adjust(100m, 200m, 100m, 90m, 90m).Cancelled);
        Assert.True(GapRiskAdjuster.Adjust(100m, 200m, 100m, 90m, 85m).Cancelled);
    }

    [Fact]
    public void ABigGapThatRoundsStage1ToZero_CancelsInsteadOfBuyingNothing()
    {
        // planned 1 share at risk 10; fill 1000 above => factor ~0.01 => floor(1 x 0.01) = 0
        var r = GapRiskAdjuster.Adjust(1m, 2m, 100m, 90m, 1090m);

        Assert.True(r.Cancelled);
        Assert.Equal(0m, r.Stage1Quantity);
    }

    [Fact]
    public void DegeneratePlans_AreNotScaled_RatherThanDividingByZero()
    {
        var r = GapRiskAdjuster.Adjust(100m, 200m, plannedEntry: 90m, stop: 90m, expectedFillPrice: 100m);   // planned risk 0

        Assert.False(r.Resized); Assert.False(r.Cancelled); Assert.Equal(100m, r.Stage1Quantity);
    }

    // ---------------- inside the engine ----------------

    private static IReadOnlyList<DateOnly> Sessions(int count)
    {
        var d = new List<DateOnly>();
        for (var x = new DateOnly(2023, 1, 2); d.Count < count; x = x.AddDays(1)) if (x.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) d.Add(x);
        return d;
    }

    private static List<HistoricalBar> Series(Random rng, IReadOnlyList<DateOnly> dates, string sym, decimal p0, bool cyclic)
    {
        var list = new List<HistoricalBar>(); var p = p0; var phase = rng.Next(0, 125);
        for (var i = 0; i < dates.Count; i++)
        {
            double drift, vol, vm = 1.0;
            if (!cyclic) { drift = 0.0005; vol = 0.007; }
            else
            {
                var k = (i + phase) % 125;
                if (k < 50) { drift = 0.004; vol = 0.012; }
                else if (k < 90) { drift = 0.0; vol = 0.015 - 0.011 * (k - 50) / 40.0; vm = 1.0 - 0.6 * (k - 50) / 40.0; }
                else if (k < 105) { drift = 0.008; vol = 0.012; vm = 2.2; }
                else { drift = -0.003; vol = 0.012; }
            }
            var o = p; p = Math.Max(5m, p * (1m + (decimal)(drift + vol * (rng.NextDouble() - 0.5) * 2))); var c = p;
            var hi = Math.Max(o, c) * (1m + (decimal)(vol * rng.NextDouble() * 0.5)); var lo = Math.Min(o, c) * (1m - (decimal)(vol * rng.NextDouble() * 0.5));
            list.Add(new HistoricalBar { Symbol = sym, Timestamp = new DateTimeOffset(dates[i].ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = Math.Round(o, 2), High = Math.Round(hi, 2),
                Low = Math.Round(Math.Max(1m, lo), 2), Close = Math.Round(c, 2), AdjustedClose = Math.Round(c, 2), Volume = (long)(1_500_000 * vm * (0.8 + 0.4 * rng.NextDouble())), Source = "s", Timeframe = Timeframe.Daily });
        }
        return list;
    }

    private static BacktestRunResult Run(bool guard)
    {
        var rng = new Random(123); var dates = Sessions(800);
        var bench = Series(rng, dates, "NIFTY500", 10_000m, false); Series(rng, dates, "NMIDCAP150", 9_000m, false);
        var symbols = Enumerable.Range(0, 40).Select(n => new BacktestSymbolContext { Symbol = $"SYN{n:00}", Bars = Series(rng, dates, $"SYN{n:00}", 80m + n * 7, true), MarketCapBucket = MarketCapBucket.Mid }).ToList();
        var req = new BacktestRunRequest { StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d", StartDate = dates[260], EndDate = dates[^1], SurvivorshipBiased = true, BroadIndexBars = bench, Symbols = symbols };
        var opt = new BacktestEngineOptions
        {
            Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
            MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList(), ResizeEntryForGapRisk = guard },
            Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m }, Backtest = new BacktestOptions(),
        };
        return BacktestEngine.Run(req, opt);
    }


    [Fact]
    public void Guard_On_ResizesGappedEntries_AndReportsHowFarFillsLandedFromThePivot()
    {
        var on = Run(guard: true);

        Assert.NotNull(on.EntryExecution);
        Assert.True(on.EntryExecution!.GuardEnabled);
        Assert.True(on.EntryExecution.MeanFillVsPlannedEntryPercent > 0m, "breakouts in this universe open above the pivot on average");
        Assert.True(on.EntryExecution.Resized > 0, "some entries gap enough to need scaling");
    }

    [Fact]
    public void Guard_Off_ChangesNothing_AndStillReportsTheGaps()
    {
        var off = Run(guard: false);

        Assert.False(off.EntryExecution!.GuardEnabled);
        Assert.Equal(0, off.EntryExecution.Resized);
        Assert.Equal(0, off.EntryExecution.Cancelled);
        Assert.True(off.EntryExecution.MeanFillVsPlannedEntryPercent > 0m);
    }

    [Fact]
    public void Guard_On_RecordsRiskFromTheActualFill_SoTheGuardChangesRMultiples()
    {
        var on = Run(guard: true); var off = Run(guard: false);

        // Same signals and the same stops; only the accounting/sizing differs. Reported R (pnl / initial risk) must be lower with honest
        // accounting, because the actual risk carried is larger than the planned pivot-based risk that the legacy path divides by.
        var avgR = (BacktestRunResult r) => r.Trades.Average(t => t.RMultiple);
        Assert.True(avgR(on) < avgR(off), $"honest R {avgR(on):F2} should be below the planned-risk R {avgR(off):F2}");
    }

    [Fact]
    public void Report_DescribesTheEntryExecutionAndTheGuard()
    {
        var on = BacktestReport.Build(Run(guard: true), 1_000_000m);
        var off = BacktestReport.Build(Run(guard: false), 1_000_000m);

        Assert.True(on.VerdictNotes!.Any(n => n.Contains("Entry execution") && n.Contains("scaled")));
        Assert.True(off.VerdictNotes!.Any(n => n.Contains("Entry execution") && n.Contains("OFF")));
    }
}
