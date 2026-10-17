using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class AntiOverfittingCheckerTests
{
    private static IReadOnlyList<Core.Models.Trade> GoodTrades(int count) =>
        Enumerable.Range(0, count).Select(i => SelectionFixture.MakeTrade("ABC", i % 4 == 0 ? -200m : 400m, entryAt: new DateTimeOffset(2020 + i % 3, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)))).ToList();

    [Fact]
    public void ExcessiveDrawdown_Over40Percent_TriggersAutomaticRejection()
    {
        var result = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 45m);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, maxAcceptableDrawdownPercent: 40m, minimumTradeCount: 30, aggressiveRegimeScoreThreshold: 80);

        Assert.True(report.Rejected);
        Assert.True(report.Flags.Any(f => f.Name == "ExcessiveDrawdown" && f.Triggered));
    }

    [Fact]
    public void AcceptableDrawdown_DoesNotRejectOnItsOwn()
    {
        var result = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 20m);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, maxAcceptableDrawdownPercent: 40m, minimumTradeCount: 30, aggressiveRegimeScoreThreshold: 80);

        Assert.False(report.Rejected);
    }

    [Fact]
    public void TooFewTrades_Flags_ButNeverAutoRejectsOnItsOwn()
    {
        var result = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1), GoodTrades(5), maxDrawdownPercent: 15m);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, maxAcceptableDrawdownPercent: 40m, minimumTradeCount: 30, aggressiveRegimeScoreThreshold: 80);

        Assert.True(report.Flags.Any(f => f.Name == "TooFewTrades" && f.Triggered));
        Assert.False(report.Rejected); // "too few trades" alone is a flag, not one of spec's two disqualifying conditions
    }

    [Fact]
    public void OutOfSamplePerformanceDisappears_TriggersAutomaticRejection()
    {
        var goodTrain = SelectionFixture.MakeResult(new(2010, 1, 1), new(2013, 1, 1), GoodTrades(40), cagr: 25m, sharpe: 1.5m, maxDrawdownPercent: 10m);
        var badTest = SelectionFixture.MakeResult(new(2013, 7, 2), new(2014, 1, 1), GoodTrades(5), cagr: -10m, sharpe: -1m, maxDrawdownPercent: 30m);
        var validate = SelectionFixture.MakeResult(new(2013, 1, 2), new(2013, 7, 1), GoodTrades(10), cagr: 5m, sharpe: 0.2m, maxDrawdownPercent: 15m);

        var windows = new[]
        {
            new WalkForwardWindowResult(
                new WalkForwardWindow(new(2010,1,1), new(2013,1,1), new(2013,1,2), new(2013,7,1), new(2013,7,2), new(2014,1,1)),
                goodTrain, validate, badTest),
        };

        var fullResult = SelectionFixture.MakeResult(new(2010, 1, 1), new(2014, 1, 1), GoodTrades(40), maxDrawdownPercent: 15m);
        var report = AntiOverfittingChecker.Evaluate(fullResult, windows, null, maxAcceptableDrawdownPercent: 40m, minimumTradeCount: 30, aggressiveRegimeScoreThreshold: 80);

        Assert.True(report.Rejected);
        Assert.True(report.Flags.Any(f => f.Name == "OutOfSamplePerformanceDisappears" && f.Triggered));
    }

    [Fact]
    public void SingleStockDependence_FlaggedWhenOneSymbolDominates()
    {
        var result = SelectionFixture.MakeResult(new(2020, 1, 1), new(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 10m);
        var tradeCountBySymbol = new Dictionary<string, int> { ["ABC"] = 35, ["XYZ"] = 5 };

        var report = AntiOverfittingChecker.Evaluate(result, null, tradeCountBySymbol, 40m, 30, 80, maxSingleDependenceSharePercent: 50m);

        Assert.True(report.Flags.Any(f => f.Name == "SingleStockDependence" && f.Triggered));
    }

    [Fact]
    public void SingleStockDependence_NotFlagged_WhenWellDiversified()
    {
        var result = SelectionFixture.MakeResult(new(2020, 1, 1), new(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 10m);
        var tradeCountBySymbol = new Dictionary<string, int> { ["ABC"] = 10, ["XYZ"] = 10, ["DEF"] = 10, ["GHI"] = 10 };

        var report = AntiOverfittingChecker.Evaluate(result, null, tradeCountBySymbol, 40m, 30, 80, maxSingleDependenceSharePercent: 50m);

        Assert.False(report.Flags.Any(f => f.Name == "SingleStockDependence" && f.Triggered));
    }

    [Fact]
    public void BullMarketDependence_FlaggedWhenAllTradesEnteredInAggressiveRegime()
    {
        var trades = Enumerable.Range(0, 30).Select(i => SelectionFixture.MakeTrade("ABC", 100m, regimeScoreAtEntry: 90)).ToList();
        var result = SelectionFixture.MakeResult(new(2020, 1, 1), new(2023, 1, 1), trades, maxDrawdownPercent: 10m);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, 40m, 30, aggressiveRegimeScoreThreshold: 80, maxSingleDependenceSharePercent: 50m);

        Assert.True(report.Flags.Any(f => f.Name == "BullMarketDependence" && f.Triggered));
    }

    [Fact]
    public void SurvivorshipBiased_FlaggedFromRunRecord()
    {
        var result = SelectionFixture.MakeResult(new(2020, 1, 1), new(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 10m, survivorshipBiased: true);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, 40m, 30, 80);

        Assert.True(report.Flags.Any(f => f.Name == "SurvivorshipBiased" && f.Triggered));
    }

    [Fact]
    public void LookAheadBiasFlag_IsAlwaysPresent_AsADocumentedGuarantee_NeverTriggered()
    {
        var result = SelectionFixture.MakeResult(new(2020, 1, 1), new(2023, 1, 1), GoodTrades(40), maxDrawdownPercent: 10m);

        var report = AntiOverfittingChecker.Evaluate(result, null, null, 40m, 30, 80);

        var flag = report.Flags.Single(f => f.Name == "LookAheadBiasStructurallyPrevented");
        Assert.False(flag.Triggered);
    }
}
