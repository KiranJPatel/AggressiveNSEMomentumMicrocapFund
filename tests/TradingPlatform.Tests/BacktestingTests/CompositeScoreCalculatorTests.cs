using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class CompositeScoreCalculatorTests
{
    [Fact]
    public void CalculateSingleRun_HigherCagr_ProducesHigherScore_AllElseEqual()
    {
        var lowResult = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            Enumerable.Range(0, 30).Select(i => SelectionFixture.MakeTrade("ABC", 10m)).ToList(), cagr: 5m, sharpe: 1m);
        var highResult = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            Enumerable.Range(0, 30).Select(i => SelectionFixture.MakeTrade("ABC", 10m)).ToList(), cagr: 25m, sharpe: 1m);

        var lowScore = CompositeScoreCalculator.CalculateSingleRun(lowResult.Run);
        var highScore = CompositeScoreCalculator.CalculateSingleRun(highResult.Run);

        Assert.True(highScore > lowScore);
    }

    [Fact]
    public void CalculateSingleRun_DeeperDrawdown_ProducesLowerScore()
    {
        var shallow = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m, maxDrawdownPercent: 10m);
        var deep = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m, maxDrawdownPercent: 35m);

        Assert.True(CompositeScoreCalculator.CalculateSingleRun(shallow.Run) > CompositeScoreCalculator.CalculateSingleRun(deep.Run));
    }

    [Fact]
    public void CalculateSingleRun_TooFewTrades_PenalizesScore()
    {
        var fewTrades = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            Enumerable.Range(0, 3).Select(i => SelectionFixture.MakeTrade("ABC", 10m)).ToList(), cagr: 15m, sharpe: 1m);
        var manyTrades = SelectionFixture.MakeResult(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            Enumerable.Range(0, 50).Select(i => SelectionFixture.MakeTrade("ABC", 10m)).ToList(), cagr: 15m, sharpe: 1m);

        Assert.True(CompositeScoreCalculator.CalculateSingleRun(manyTrades.Run, 30) > CompositeScoreCalculator.CalculateSingleRun(fewTrades.Run, 30));
    }

    [Fact]
    public void CalculateWalkForwardStability_ConsistentTestPerformance_ScoresHigherThanVolatile()
    {
        var consistentWindows = new[]
        {
            new WalkForwardWindowResult(
                new WalkForwardWindow(new(2010,1,1), new(2013,1,1), new(2013,1,2), new(2013,7,1), new(2013,7,2), new(2014,1,1)),
                SelectionFixture.MakeResult(new(2010,1,1), new(2013,1,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,1,2), new(2013,7,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,7,2), new(2014,1,1), Array.Empty<Core.Models.Trade>(), cagr: 14m, sharpe: 1m)),
            new WalkForwardWindowResult(
                new WalkForwardWindow(new(2010,7,1), new(2013,7,1), new(2013,7,2), new(2014,1,1), new(2014,1,2), new(2014,7,1)),
                SelectionFixture.MakeResult(new(2010,7,1), new(2013,7,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,7,2), new(2014,1,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2014,1,2), new(2014,7,1), Array.Empty<Core.Models.Trade>(), cagr: 16m, sharpe: 1m)),
        };

        var volatileWindows = new[]
        {
            new WalkForwardWindowResult(
                new WalkForwardWindow(new(2010,1,1), new(2013,1,1), new(2013,1,2), new(2013,7,1), new(2013,7,2), new(2014,1,1)),
                SelectionFixture.MakeResult(new(2010,1,1), new(2013,1,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,1,2), new(2013,7,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,7,2), new(2014,1,1), Array.Empty<Core.Models.Trade>(), cagr: 40m, sharpe: 3m)),
            new WalkForwardWindowResult(
                new WalkForwardWindow(new(2010,7,1), new(2013,7,1), new(2013,7,2), new(2014,1,1), new(2014,1,2), new(2014,7,1)),
                SelectionFixture.MakeResult(new(2010,7,1), new(2013,7,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2013,7,2), new(2014,1,1), Array.Empty<Core.Models.Trade>(), cagr: 15m, sharpe: 1m),
                SelectionFixture.MakeResult(new(2014,1,2), new(2014,7,1), Array.Empty<Core.Models.Trade>(), cagr: -20m, sharpe: -1m)),
        };

        var consistentScore = CompositeScoreCalculator.CalculateWalkForwardStability(consistentWindows);
        var volatileScore = CompositeScoreCalculator.CalculateWalkForwardStability(volatileWindows);

        Assert.True(consistentScore > volatileScore);
    }

    [Fact]
    public void CalculateWalkForwardStability_EmptyList_ReturnsZero()
    {
        Assert.Equal(0m, CompositeScoreCalculator.CalculateWalkForwardStability(Array.Empty<WalkForwardWindowResult>()));
    }
}
