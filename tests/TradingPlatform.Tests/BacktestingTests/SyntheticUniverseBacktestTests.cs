using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>
/// A multi-symbol run over synthetic trend / contraction / breakout / pullback cycles. The single-stock fixtures used elsewhere never queue a
/// pyramid add-on for a position that exits before the add-on fills; this universe does (it is the data that first exposed the
/// "Opening a new position in SYN34 requires InitialStop/..." abort, which the ORIGINAL post-audit engine also had).
/// The numbers it produces mean nothing about real markets — it only checks the engine survives and stays consistent.
/// </summary>
public class SyntheticUniverseBacktestTests
{
    private static List<DateOnly> Sessions(int count)
    {
        var dates = new List<DateOnly>();
        for (var d = new DateOnly(2023, 1, 2); dates.Count < count; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dates.Add(d);
        return dates;
    }

    private static List<HistoricalBar> Walk(Random rng, IReadOnlyList<DateOnly> dates, string sym, decimal p0, bool cyclic, long baseVol)
    {
        var list = new List<HistoricalBar>(); var p = p0; var phase = rng.Next(0, 125);
        for (var i = 0; i < dates.Count; i++)
        {
            double drift, vol, volMul = 1.0;
            if (!cyclic) { drift = 0.0005; vol = 0.007; }
            else
            {
                var k = (i + phase) % 125;
                if (k < 50) { drift = 0.004; vol = 0.012; }
                else if (k < 90) { drift = 0.0; vol = 0.015 - 0.011 * (k - 50) / 40.0; volMul = 1.0 - 0.6 * (k - 50) / 40.0; }
                else if (k < 105) { drift = 0.008; vol = 0.012; volMul = 2.2; }
                else { drift = -0.003; vol = 0.012; }
            }
            var o = p; p = Math.Max(5m, p * (1m + (decimal)(drift + vol * (rng.NextDouble() - 0.5) * 2)));
            var c = p; var hi = Math.Max(o, c) * (1m + (decimal)(vol * rng.NextDouble() * 0.5)); var lo = Math.Min(o, c) * (1m - (decimal)(vol * rng.NextDouble() * 0.5));
            list.Add(new HistoricalBar
            {
                Symbol = sym, Timestamp = new DateTimeOffset(dates[i].ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = Math.Round(o, 2), High = Math.Round(hi, 2),
                Low = Math.Round(Math.Max(1m, lo), 2), Close = Math.Round(c, 2), AdjustedClose = Math.Round(c, 2), Volume = (long)(baseVol * volMul * (0.8 + 0.4 * rng.NextDouble())),
                Source = "synthetic", Timeframe = Timeframe.Daily,
            });
        }
        return list;
    }

    private static BacktestRunResult Run(int symbolCount, int days, int seed, bool gapGuard = true)
    {
        var rng = new Random(seed);
        var dates = Sessions(days);
        var benchmark = Walk(rng, dates, "NIFTY500", 10_000m, false, 0);
        Walk(rng, dates, "NMIDCAP150", 9_000m, false, 0);   // draws consumed exactly as the seeding tool did, so the series are reproducible
        var symbols = Enumerable.Range(0, symbolCount).Select(n => new BacktestSymbolContext
        {
            Symbol = $"SYN{n:00}", Bars = Walk(rng, dates, $"SYN{n:00}", 80m + n * 7, true, 1_500_000), MarketCapBucket = MarketCapBucket.Mid,
        }).ToList();

        var request = new BacktestRunRequest
        {
            StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d", StartDate = dates[260], EndDate = dates[^1],
            SurvivorshipBiased = true, BroadIndexBars = benchmark, Symbols = symbols,
        };
        var options = new BacktestEngineOptions
        {
            Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
            MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList(), ResizeEntryForGapRisk = gapGuard },
            Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m }, Backtest = new BacktestOptions(),
        };
        return BacktestEngine.Run(request, options);
    }

    [Fact]
    public void MultiSymbolUniverse_CompletesWithoutAbort_AndTheResultIsInternallyConsistent()
    {
        var r = Run(symbolCount: 40, days: 800, seed: 123);

        Assert.True(r.Trades.Count > 10, $"the synthetic cycles should produce trades (got {r.Trades.Count})");
        // Every closed trade belongs to a distinct entry; nothing is entered twice at the same instant for the same symbol.
        Assert.Equal(r.Trades.Count, r.Trades.Select(t => (t.Symbol, t.EntryAt)).Distinct().Count());
        // Cash/NAV never go negative, and the equity curve covers the whole simulated range.
        Assert.True(r.DailySnapshots.All(s => s.Nav > 0m && s.CashBalance >= -1m), "NAV must stay positive and cash must not go materially negative");
        Assert.True(r.DailySnapshots.Count > 400);
        // Re-running is deterministic.
        Assert.Equal(r.Run.FinalCapital, Run(40, 800, 123).Run.FinalCapital);
    }

    [Fact]
    public void MultiSymbolUniverse_AlsoCompletesWithTheGapGuardOff()
    {
        var r = Run(symbolCount: 40, days: 800, seed: 123, gapGuard: false);

        Assert.True(r.Trades.Count > 10);
        Assert.False(r.EntryExecution!.GuardEnabled);
        Assert.Equal(0, r.EntryExecution.Resized);   // OFF means nothing is touched
    }

    [Fact]
    public void OrphanedPyramidAddOns_AreCancelledAndCounted_NotFilledIntoAPositionThatNoLongerExists()
    {
        // Guard OFF reproduces the original sizing dynamics, under which this universe queues an add-on for a position that exits first.
        // (With the gap-risk guard on, position sizes — and so exit timing — differ and this particular coincidence no longer occurs.)
        var r = Run(symbolCount: 40, days: 800, seed: 123, gapGuard: false);

        Assert.True(r.OrphanedAddOnOrdersCancelled >= 1, "this universe queues an add-on whose position exits first; the engine must cancel it (and say so)");
        var report = TradingPlatform.Reporting.Reports.BacktestReport.Build(r, 1_000_000m);
        Assert.True(report.VerdictNotes!.Any(n => n.Contains("pyramid add-on")));
    }
}
