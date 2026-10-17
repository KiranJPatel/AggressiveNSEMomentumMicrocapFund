using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.Execution;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>Spec §3.8 / §7.3: 60m entry refinement and 15m execution, both as pure rules and inside the backtest engine.</summary>
public class IntradayRefinementTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateOnly Day = new(2026, 3, 10);

    private static HistoricalBar H(int hour, int minute, decimal open, decimal close, long volume, Timeframe tf = Timeframe.H1, DateOnly? day = null) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset((day ?? Day).ToDateTime(new TimeOnly(hour, minute)), Ist), Open = open, High = Math.Max(open, close) + 0.1m,
        Low = Math.Min(open, close) - 0.1m, Close = close, AdjustedClose = close, Volume = volume, Source = "t", Timeframe = tf,
    };

    /// <summary>Hourly session 09:15..15:15 with the given closes (open = previous close, first open = first close).</summary>
    private static List<HistoricalBar> Session(decimal[] closes, long[]? volumes = null, DateOnly? day = null)
    {
        var list = new List<HistoricalBar>();
        for (var i = 0; i < closes.Length; i++)
            list.Add(H(9 + i, 15, i == 0 ? closes[0] : closes[i - 1], closes[i], volumes?[i] ?? 1000, Timeframe.H1, day));
        return list;
    }

    // ---------------- pure refiner ----------------

    [Fact]
    public void NextOpen_NeedsNoRefinement_AndMissingBarsAreReportedAsNoData()
    {
        Assert.Throws<ArgumentException>(() => IntradayEntryRefiner.Refine(SignalToOrderLag.NextOpen, 100m, null, null, null, 0m, 1m));
        Assert.Equal(RefinementOutcome.NoIntradayData, IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, null, null, null, 0m, 1m).Outcome);
        Assert.Equal(RefinementOutcome.NoIntradayData, IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, new List<HistoricalBar>(), null, null, 0m, 1m).Outcome);
    }

    [Fact]
    public void Hourly_FillsAtTheOpenOfTheBarAfterTheFirstCloseAboveTheLevel()
    {
        var hours = Session(new[] { 99m, 100.5m, 102m, 103m, 103.5m, 104m, 104.5m });

        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, hours, null, null, 0m, 1m);

        Assert.Equal(RefinementOutcome.Executed, r.Outcome);
        Assert.Equal(new TimeSpan(10, 15, 0), r.TriggerBarStart!.Value.TimeOfDay);   // second bar closed 100.5 > 100
        Assert.Equal(100.5m, r.ExecutionPrice);                                       // third bar opens at the previous close
        Assert.Equal(new TimeSpan(11, 15, 0), r.ExecutionTime!.Value.TimeOfDay);
        Assert.False(r.UsedQuarterHour);
    }

    [Fact]
    public void TheBuffer_RaisesTheConfirmationLevel()
    {
        var hours = Session(new[] { 99m, 100.5m, 102m, 103m, 103.5m, 104m, 104.5m });

        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, hours, null, null, confirmBufferPercent: 1m, 1m);   // must close above 101

        Assert.Equal(new TimeSpan(11, 15, 0), r.TriggerBarStart!.Value.TimeOfDay);   // the 100.5 close no longer counts; 102 does
    }

    [Fact]
    public void TheVolumeCondition_SkipsAThinConfirmation_AndIsNotEvaluatedWithoutABaseline()
    {
        var hours = Session(new[] { 99m, 101m, 102m, 103m, 103m, 103m, 103m }, volumes: new long[] { 1000, 400, 1600, 1000, 1000, 1000, 1000 });

        var withBaseline = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, hours, null, baselineHourlyVolume: 1000m, 0m, minTriggerRelativeVolume: 1m);
        var noBaseline = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, hours, null, baselineHourlyVolume: null, 0m, 1m);

        Assert.Equal(new TimeSpan(11, 15, 0), withBaseline.TriggerBarStart!.Value.TimeOfDay);   // the 400-share bar is too thin; the 1600 one confirms
        Assert.Equal(new TimeSpan(10, 15, 0), noBaseline.TriggerBarStart!.Value.TimeOfDay);
    }

    [Fact]
    public void NoCloseAboveTheLevel_CancelsTheEntry()
    {
        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, Session(new[] { 99m, 98m, 99.5m, 99.9m, 100m, 99m, 98m }), null, null, 0m, 1m);

        Assert.Equal(RefinementOutcome.NoConfirmation, r.Outcome);
        Assert.Null(r.ExecutionPrice);
    }

    [Fact]
    public void ConfirmationOnTheLastHourlyBar_HasNothingLeftToExecuteOn()
    {
        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement60m, 100m, Session(new[] { 99m, 99m, 99m, 99m, 99m, 99m, 101m }), null, null, 0m, 1m);

        Assert.Equal(RefinementOutcome.NoLaterBarToExecute, r.Outcome);
    }

    [Fact]
    public void QuarterHour_ExecutesAtTheFirst15mBarThatStartsAfterTheConfirmingHourEnds()
    {
        var hours = Session(new[] { 99m, 101m, 102m, 103m, 103m, 103m, 103m });                // confirmation: 10:15 bar, which ends 11:15
        var quarters = new List<HistoricalBar>
        {
            H(10, 45, 101.2m, 101.3m, 10, Timeframe.M15), H(11, 0, 101.4m, 101.5m, 10, Timeframe.M15),
            H(11, 15, 101.7m, 101.8m, 10, Timeframe.M15), H(11, 30, 101.9m, 102m, 10, Timeframe.M15),
        };

        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement15m, 100m, hours, quarters, null, 0m, 1m);

        Assert.Equal(101.7m, r.ExecutionPrice);
        Assert.Equal(new TimeSpan(11, 15, 0), r.ExecutionTime!.Value.TimeOfDay);
        Assert.True(r.UsedQuarterHour);
    }

    [Fact]
    public void QuarterHour_WithNo15mBars_FallsBackToTheNextHourlyOpen_AndSaysSo()
    {
        var r = IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement15m, 100m, Session(new[] { 99m, 101m, 102m, 103m, 103m, 103m, 103m }), null, null, 0m, 1m);

        Assert.Equal(RefinementOutcome.Executed, r.Outcome);
        Assert.Equal(101m, r.ExecutionPrice);
        Assert.False(r.UsedQuarterHour);
        Assert.True(r.Reason.Contains("no 15m bars"));
    }

    [Fact]
    public void TheLastHourlyCandle_EndsAtTheClose_SoALateConfirmationCanStillExecuteOnThe1515Bar()
    {
        var hours = Session(new[] { 99m, 99m, 99m, 99m, 99m, 101m, 99m });                       // confirmation on the 14:15 bar (ends 15:15)
        var quarters = new List<HistoricalBar> { H(15, 15, 101.4m, 101.5m, 10, Timeframe.M15) };

        Assert.Equal(101.4m, IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement15m, 100m, hours, quarters, null, 0m, 1m).ExecutionPrice);

        var late = Session(new[] { 99m, 99m, 99m, 99m, 99m, 99m, 101m });                       // confirmation on the 15:15 bar (ends 15:30 = close)
        Assert.Equal(RefinementOutcome.NoLaterBarToExecute, IntradayEntryRefiner.Refine(SignalToOrderLag.Refinement15m, 100m, late, quarters, null, 0m, 1m).Outcome);
    }

    [Fact]
    public void Tally_AveragesTheFillVersusTheDailyOpen()
    {
        var t = new RefinementTally();
        t.RecordExecution(102m, 100m); t.RecordExecution(99m, 100m);

        var s = t.ToStats(SignalToOrderLag.Refinement60m);

        Assert.Equal(2, s.Executed);
        Assert.Equal(0.5m, s.MeanExecutionVsDailyOpenPercent);
    }

    // ---------------- inside the engine ----------------

    private static BacktestEngineOptions Options(SignalToOrderLag mode, bool skipWhenNoData = false) => new()
    {
        Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
        MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
        Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m },
        Backtest = new BacktestOptions { SignalToOrderLag = mode, RefinementSkipWhenNoIntradayData = skipWhenNoData },
    };

    private static BacktestRunRequest Request(IReadOnlyList<HistoricalBar>? hourly = null, IReadOnlyList<HistoricalBar>? quarter = null)
    {
        var stock = BacktestEngineFixture.BuildStockBars();
        return new BacktestRunRequest
        {
            StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d",
            StartDate = DateOnly.FromDateTime(stock[0].Timestamp.Date), EndDate = DateOnly.FromDateTime(stock[^1].Timestamp.Date),
            SurvivorshipBiased = true, BroadIndexBars = BacktestEngineFixture.BuildFlatBenchmark(stock.Count),
            Symbols = new[] { new BacktestSymbolContext { Symbol = BacktestEngineFixture.Symbol, Bars = stock, MarketCapBucket = MarketCapBucket.Mid, HourlyBars = hourly, QuarterHourBars = quarter } },
        };
    }

    /// <summary>Hourly bars for the session the baseline run enters on: all closes far above any breakout level, the second bar's open (= the refined fill) is the given price.</summary>
    private static List<HistoricalBar> ConfirmingSession(DateOnly entryDate, decimal fillOpen)
    {
        var bars = new List<HistoricalBar>();
        for (var i = 0; i < 7; i++)
            bars.Add(H(9 + i, 15, i == 1 ? fillOpen : 5_000m, 5_000m, 1000, Timeframe.H1, entryDate));
        return bars;
    }

    [Fact]
    public void WithoutRefinement_TheRunHasNoRefinementStats_AndTheGoldenTradeIsUnchanged()
    {
        var baseline = BacktestEngine.Run(Request(), Options(SignalToOrderLag.NextOpen));

        Assert.Equal(1, baseline.Trades.Count);
        Assert.Null(baseline.Refinement);
    }

    [Fact]
    public void ConfirmedEntry_FillsAtTheRefinedPrice_NotTheDailyOpen()
    {
        var baseline = BacktestEngine.Run(Request(), Options(SignalToOrderLag.NextOpen));
        var entryDate = DateOnly.FromDateTime(baseline.Trades[0].EntryAt.Date);
        var dailyOpen = baseline.Trades[0].EntryPrice;
        var refinedOpen = Math.Round(dailyOpen * 1.02m, 2);

        var refined = BacktestEngine.Run(Request(ConfirmingSession(entryDate, refinedOpen)), Options(SignalToOrderLag.Refinement60m));

        Assert.Equal(1, refined.Trades.Count);
        Assert.True(refined.Trades[0].EntryPrice > dailyOpen * 1.01m, $"refined fill {refined.Trades[0].EntryPrice} should sit about 2% above the daily-open fill {dailyOpen}");
        Assert.Equal(1, refined.Refinement!.Executed);
        Assert.Equal(1, refined.Refinement.Attempted);
        Assert.True(refined.Refinement.MeanExecutionVsDailyOpenPercent > 0m);
    }

    [Fact]
    public void UnconfirmedBreakout_CancelsTheEntry()
    {
        var baseline = BacktestEngine.Run(Request(), Options(SignalToOrderLag.NextOpen));
        var entryDate = DateOnly.FromDateTime(baseline.Trades[0].EntryAt.Date);
        var weak = Enumerable.Range(0, 7).Select(i => H(9 + i, 15, 0.5m, 0.5m, 1000, Timeframe.H1, entryDate)).ToList();   // never closes above any level

        // skipWhenNoData: the cancelled breakout is re-signalled on later sessions (the setup still holds); those sessions have no intraday bars
        // here, and with the skip option they are cancelled too — so the only way a trade could appear is if the first-day cancellation failed.
        var refined = BacktestEngine.Run(Request(weak), Options(SignalToOrderLag.Refinement60m, skipWhenNoData: true));

        Assert.Equal(0, refined.Trades.Count);
        Assert.Equal(1, refined.Refinement!.SkippedNoConfirmation);                 // exactly the one session that HAS bars
        Assert.True(refined.Refinement.SkippedNoIntradayData >= 1);
    }

    [Fact]
    public void NoIntradayData_FallsBackToTheDailyOpen_ByDefault_ButIsCounted()
    {
        var baseline = BacktestEngine.Run(Request(), Options(SignalToOrderLag.NextOpen));

        var refined = BacktestEngine.Run(Request(), Options(SignalToOrderLag.Refinement60m));

        Assert.Equal(baseline.Trades[0].EntryPrice, refined.Trades[0].EntryPrice);
        Assert.Equal(1, refined.Refinement!.FallbackToDailyOpen);
        Assert.Equal(0, refined.Refinement.Executed);
    }

    [Fact]
    public void NoIntradayData_CanInsteadSkipTheEntry()
    {
        var refined = BacktestEngine.Run(Request(), Options(SignalToOrderLag.Refinement60m, skipWhenNoData: true));

        Assert.Equal(0, refined.Trades.Count);
        // The entry is cancelled and re-signalled each session while the breakout setup holds, so it is counted once per session.
        Assert.True(refined.Refinement!.SkippedNoIntradayData >= 1);
        Assert.Equal(0, refined.Refinement.FallbackToDailyOpen);
    }
}
