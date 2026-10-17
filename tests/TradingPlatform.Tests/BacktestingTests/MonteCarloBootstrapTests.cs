using TradingPlatform.Application.Cli;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.CliTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class MonteCarloBootstrapTests
{
    private const decimal Capital = 1_000_000m;

    private static List<Trade> MixedTrades()
    {
        // 12 winners of +2R and 18 losers of -1R at 1% risk: positive expectancy, wide outcome spread.
        var trades = new List<Trade>();
        for (var i = 0; i < 30; i++)
        {
            var win = i % 5 < 2; // 12 of 30
            var t = SelectionFixture.MakeTrade("S" + i, win ? 20_000m : -10_000m, initialRiskAmount: 10_000m);
            t.RiskAtEntryPercent = 1m;
            trades.Add(t);
        }
        return trades;
    }

    [Fact]
    public void Reshuffle_CannotSpreadEndingCapital_ItIsPathRiskOnly()
    {
        var r = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 400, new Random(7), mode: MonteCarloMode.Reshuffle);

        Assert.Equal(MonteCarloMode.Reshuffle, r.Mode);
        Assert.True((r.P95EndingCapital - r.P5EndingCapital) / Capital < 0.02m, "the sum of P&L is invariant, so only jitter can move the ending capital");
    }

    [Fact]
    public void Bootstrap_SpreadsEndingCapitalWidely()
    {
        var r = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 400, new Random(7), mode: MonteCarloMode.Bootstrap);

        Assert.Equal(MonteCarloMode.Bootstrap, r.Mode);
        Assert.True((r.P95EndingCapital - r.P5EndingCapital) / Capital > 0.10m, $"P5 {r.P5EndingCapital:N0} .. P95 {r.P95EndingCapital:N0} should be far apart when resampling");
        Assert.True(r.P5EndingCapital <= r.P25EndingCapital && r.P25EndingCapital <= r.MedianEndingCapital && r.MedianEndingCapital <= r.P75EndingCapital && r.P75EndingCapital <= r.P95EndingCapital);
    }

    [Fact]
    public void Bootstrap_IsReproducibleForAGivenSeed_AndDiffersAcrossSeeds()
    {
        var a = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 200, new Random(11), mode: MonteCarloMode.Bootstrap);
        var b = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 200, new Random(11), mode: MonteCarloMode.Bootstrap);
        var c = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 200, new Random(12), mode: MonteCarloMode.Bootstrap);

        Assert.Equal(a, b);
        Assert.NotEqual(a.MedianEndingCapital, c.MedianEndingCapital);
    }

    private static readonly DateTimeOffset T0 = new(2022, 1, 3, 0, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Bootstrap_CompoundsOnRunningCapital_UsingTheEquityAtEachEntry()
    {
        // 10 back-to-back trades (entry every 20 days, 10 days long), each +10,000 rupees. Measured against the equity at ITS OWN entry
        // (1,000,000, 1,010,000, ...) the returns shrink slightly, and compounding them lands exactly on the real total: +100,000.
        var trades = Enumerable.Range(0, 10).Select(i => SelectionFixture.MakeTrade("S" + i, 10_000m, entryAt: T0.AddDays(20 * i))).ToList();

        var r = MonteCarloSimulator.Simulate(trades, Capital, 50, new Random(1), slippageJitterPercent: 0m, mode: MonteCarloMode.Bootstrap);

        // Bootstrap draws trades WITH replacement, so each path mixes the ten (slightly different, 0.92-1.00%) returns differently and the median is
        // not exactly the original path. It must still land within a hair of the real +100,000 — the old R x configured-risk formula could not.
        Assert.True(Math.Abs(r.MedianEndingCapital - 1_100_000m) < 1_000m, $"median {r.MedianEndingCapital:N0} should be within 0.1% of 1,100,000");
        Assert.Equal(0m, r.ProbabilityOfRuin);
    }

    [Fact]
    public void EquityReturns_UseActualPnlOverEquityAtEntry_NotRMultipleTimesConfiguredRisk()
    {
        // The case the old formula got wrong: sized far below the configured 1% risk (cap-limited), so a 5R winner earned only 0.5% of equity.
        var capped = SelectionFixture.MakeTrade("S", 5_000m, initialRiskAmount: 1_000m, entryAt: T0);   // RMultiple 5, RiskAtEntryPercent 1 => old formula said +5%
        Assert.Equal(5m, capped.RMultiple);

        Assert.Equal(0.005m, MonteCarloSimulator.EquityReturns(new[] { capped }, Capital)[0]);          // actual: 5,000 / 1,000,000

        var r = MonteCarloSimulator.Simulate(new[] { capped }, Capital, 20, new Random(2), slippageJitterPercent: 0m, mode: MonteCarloMode.Bootstrap);
        Assert.Equal(1_005_000m, r.MedianEndingCapital);
    }

    [Fact]
    public void EquityReturns_CountOnlyTradesAlreadyExitedAtEntry_AndTreatNonPositiveEquityAsTotalLoss()
    {
        var first = SelectionFixture.MakeTrade("A", 100_000m, entryAt: T0);                  // exits T0+10d
        var overlapping = SelectionFixture.MakeTrade("B", 50_000m, entryAt: T0.AddDays(5));  // entered before A exited: A's P&L must NOT count
        var after = SelectionFixture.MakeTrade("C", 55_000m, entryAt: T0.AddDays(11));       // entered after A exited: equity 1,100,000

        var ret = MonteCarloSimulator.EquityReturns(new[] { first, overlapping, after }, Capital);

        Assert.Equal(100_000m / 1_000_000m, ret[0]);
        Assert.Equal(50_000m / 1_000_000m, ret[1]);
        Assert.Equal(55_000m / 1_100_000m, ret[2]);

        var wipedOut = SelectionFixture.MakeTrade("W", -1_500_000m, entryAt: T0);
        var next = SelectionFixture.MakeTrade("N", 10_000m, entryAt: T0.AddDays(11));
        Assert.Equal(-1m, MonteCarloSimulator.EquityReturns(new[] { wipedOut, next }, Capital)[1]);   // equity -500,000 at that entry
    }

    [Fact]
    public void Bootstrap_AllCatastrophicLosers_FloorsEquityAtZero_AndReportsCertainRuin()
    {
        var trades = Enumerable.Range(0, 5).Select(i =>
        {
            var t = SelectionFixture.MakeTrade("S" + i, -2_000_000m, initialRiskAmount: 10_000m);
            t.RiskAtEntryPercent = 1m; // -200R at 1% risk = a -200% return on equity: more than everything
            return t;
        }).ToList();

        var r = MonteCarloSimulator.Simulate(trades, Capital, 30, new Random(3), slippageJitterPercent: 0m, mode: MonteCarloMode.Bootstrap);

        Assert.Equal(0m, r.P5EndingCapital);
        Assert.Equal(100m, r.ProbabilityOfRuin);
        Assert.True(r.P95EndingCapital >= 0m, "equity can reach zero but never go negative");
    }

    [Fact]
    public void Bootstrap_SingleTrade_IsItsPnlOverStartingCapital()
    {
        var t = SelectionFixture.MakeTrade("S", 50_000m, initialRiskAmount: 10_000m);
        var r = MonteCarloSimulator.Simulate(new[] { t }, Capital, 20, new Random(2), slippageJitterPercent: 0m, mode: MonteCarloMode.Bootstrap);

        Assert.Equal(1_050_000m, r.MedianEndingCapital); // 1,000,000 × (1 + 50,000 / 1,000,000)
    }

    [Fact]
    public void Simulate_DefaultMode_IsStillReshuffle_SoExistingCallersKeepTheirBehaviour()
    {
        var a = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 100, new Random(5));
        var b = MonteCarloSimulator.Simulate(MixedTrades(), Capital, 100, new Random(5), mode: MonteCarloMode.Reshuffle);

        Assert.Equal(a, b);
    }

    // ---------------- command surface ----------------

    [Fact]
    public void ParseModes_DefaultsToBoth_AndRejectsUnknownValues()
    {
        Assert.Equal(2, MonteCarloCommand.ParseModes(new[] { "montecarlo", "--run-id", "1" }, out var e0).Count);
        Assert.Null(e0);
        Assert.True(MonteCarloCommand.ParseModes(new[] { "montecarlo", "--mode", "BOOTSTRAP" }, out _).SequenceEqual(new[] { MonteCarloMode.Bootstrap }));
        Assert.Equal(0, MonteCarloCommand.ParseModes(new[] { "montecarlo", "--mode", "nope" }, out var e1).Count);
        Assert.True(e1!.Contains("reshuffle, bootstrap or both"));
        MonteCarloCommand.ParseModes(new[] { "montecarlo", "--mode" }, out var e2);
        Assert.NotNull(e2);
    }

    [Fact]
    public void SeedToInt_IsDeterministic_AndAcceptsEveryLong()
    {
        Assert.Equal(MonteCarloCommand.SeedToInt(42), MonteCarloCommand.SeedToInt(42));
        Assert.Equal(42, MonteCarloCommand.SeedToInt(42));
        MonteCarloCommand.SeedToInt(long.MaxValue);
        MonteCarloCommand.SeedToInt(long.MinValue);
        Assert.NotEqual(MonteCarloCommand.SeedToInt(1L), MonteCarloCommand.SeedToInt(1L + (1L << 32)));
    }

    private static Task<int> Run(CliTestHost host, params string[] args) =>
        MonteCarloCommand.RunAsync(args, host.Repo<IBacktestRunRepository>(), host.Options<PortfolioOptions>(),
            Microsoft.Extensions.Options.Options.Create(new PathsOptions { Backtests = Path.GetTempPath() }), CliTestHost.Logger);

    [Fact]
    public async Task Command_RejectsOutOfRangeIterations_BadMode_BeforeTouchingTheDatabase()
    {
        using var host = CliTestHost.Create();

        Assert.Equal(1, await Run(host, "montecarlo", "--run-id", "1", "--iterations", "0"));
        Assert.Equal(1, await Run(host, "montecarlo", "--run-id", "1", "--iterations", "-5"));
        Assert.Equal(1, await Run(host, "montecarlo", "--run-id", "1", "--iterations", "99999999999"));
        Assert.Equal(1, await Run(host, "montecarlo", "--run-id", "1", "--mode", "weird"));
    }
}
