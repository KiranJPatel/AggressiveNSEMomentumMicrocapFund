using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class MonteCarloSimulatorTests
{
    private static readonly IReadOnlyList<Core.Models.Trade> WinningTrades = Enumerable.Range(0, 20)
        .Select(i => SelectionFixture.MakeTrade("ABC", i % 3 == 0 ? -500m : 800m))
        .ToList();

    [Fact]
    public void Simulate_IsDeterministic_ForTheSameSeed()
    {
        var resultA = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 200, new Random(42));
        var resultB = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 200, new Random(42));

        Assert.Equal(resultA.MedianEndingCapital, resultB.MedianEndingCapital);
        Assert.Equal(resultA.ProbabilityOfRuin, resultB.ProbabilityOfRuin);
    }

    [Fact]
    public void Simulate_DifferentSeeds_CanProduceDifferentResults()
    {
        var resultA = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 200, new Random(1));
        var resultB = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 200, new Random(2));

        // Not a hard guarantee for every possible seed pair, but overwhelmingly likely for 200
        // iterations of a non-trivial trade set with jitter — a meaningful sanity check that
        // randomization is actually happening, not silently producing a constant result.
        Assert.True(resultA.MedianEndingCapital != resultB.MedianEndingCapital || resultA.P5EndingCapital != resultB.P5EndingCapital);
    }

    [Fact]
    public void Simulate_PercentilesAreOrdered_P5ThroughP95()
    {
        var result = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 500, new Random(7));

        Assert.True(result.P5EndingCapital <= result.P25EndingCapital);
        Assert.True(result.P25EndingCapital <= result.MedianEndingCapital);
        Assert.True(result.MedianEndingCapital <= result.P75EndingCapital);
        Assert.True(result.P75EndingCapital <= result.P95EndingCapital);
    }

    [Fact]
    public void Simulate_AllLosingTrades_HighProbabilityOfRuin()
    {
        var losingTrades = Enumerable.Range(0, 30).Select(i => SelectionFixture.MakeTrade("ABC", -20_000m)).ToList(); // 30 * -20,000 = -600,000 on a 1,000,000 base

        var result = MonteCarloSimulator.Simulate(losingTrades, 1_000_000m, 200, new Random(3), ruinThresholdPercent: 50m);

        Assert.True(result.ProbabilityOfRuin > 90m);
    }

    [Fact]
    public void Simulate_AllWinningTrades_ZeroProbabilityOfRuin_AndZeroDrawdownBreaches()
    {
        var allWinners = Enumerable.Range(0, 20).Select(i => SelectionFixture.MakeTrade("ABC", 500m)).ToList();

        var result = MonteCarloSimulator.Simulate(allWinners, 1_000_000m, 200, new Random(9), slippageJitterPercent: 0m);

        Assert.Equal(0m, result.ProbabilityOfRuin);
        Assert.Equal(0m, result.ProbabilityDrawdownExceeds20Percent);
    }

    [Fact]
    public void Simulate_ShufflePreservesTheSameSetOfTrades_NeverFabricatesOrDrops()
    {
        // With jitter at 0%, every path's ending capital must equal initialCapital + the exact sum
        // of all trades' PnL, regardless of the order they were shuffled into — proving this is a
        // pure permutation, never a resample-with-replacement that could duplicate or omit trades.
        var expectedEnding = 1_000_000m + WinningTrades.Sum(t => t.RealizedPnl);

        var result = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 50, new Random(11), slippageJitterPercent: 0m);

        Assert.Equal(expectedEnding, result.MedianEndingCapital);
        Assert.Equal(expectedEnding, result.P5EndingCapital);
        Assert.Equal(expectedEnding, result.P95EndingCapital);
    }

    [Fact]
    public void Simulate_EmptyTradeList_Throws()
    {
        Assert.Throws<ArgumentException>(() => MonteCarloSimulator.Simulate(Array.Empty<Core.Models.Trade>(), 1_000_000m, 100, new Random(1)));
    }

    [Fact]
    public void Simulate_IterationCount_MatchesRequest()
    {
        var result = MonteCarloSimulator.Simulate(WinningTrades, 1_000_000m, 137, new Random(1));
        Assert.Equal(137, result.IterationCount);
    }
}
