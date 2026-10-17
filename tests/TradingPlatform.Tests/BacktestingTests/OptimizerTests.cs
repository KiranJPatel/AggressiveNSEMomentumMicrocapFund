using System.Text.Json;
using TradingPlatform.Application.Cli;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>Spec §5.5/§5.7 optimizer: parameter catalog, grid expansion, robustness ranking, anti-overfitting gating, CLI grammar.</summary>
public class OptimizerTests
{
    private static BacktestEngineOptions Baseline() => new()
    {
        Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
        MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
        Portfolio = new PortfolioOptions(), Backtest = new BacktestOptions(),
    };

    // ---------------- catalog ----------------

    [Fact]
    public void EveryCatalogParameter_RoundTripsThroughApplyAndRead_OnAClone_WithoutTouchingTheBaseline()
    {
        foreach (var p in OptimizerParameterCatalog.All)
        {
            var baseline = Baseline();
            var before = p.Read(baseline);
            var clone = OptimizerParameterCatalog.Clone(baseline);
            var probe = p.DefaultValues.First(v => v != before);

            p.Apply(clone, probe);

            Assert.Equal(probe, p.Read(clone), $"{p.Name}: Apply/Read disagree");
            Assert.Equal(before, p.Read(baseline), $"{p.Name}: mutating the clone leaked into the baseline");
        }
    }

    [Fact]
    public void Catalog_HasUniqueNames_AndEveryDefaultLadderIsAscendingAndNonEmpty()
    {
        Assert.Equal(OptimizerParameterCatalog.All.Count, OptimizerParameterCatalog.All.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var p in OptimizerParameterCatalog.All)
        {
            Assert.True(p.DefaultValues.Count >= 2, $"{p.Name} needs at least 2 default values");
            Assert.True(p.DefaultValues.SequenceEqual(p.DefaultValues.OrderBy(v => v)), $"{p.Name} default ladder must be ascending");
        }
    }

    [Fact]
    public void Baseline_OptionSet_SatisfiesEveryCatalogInvariant()
    {
        Assert.Null(OptimizerParameterCatalog.Validate(Baseline()));
    }

    [Fact]
    public void Validate_FlagsEachBrokenInvariant()
    {
        var o = Baseline(); o.Indicator.HmaFastPeriod = 30;
        Assert.True(OptimizerParameterCatalog.Validate(o)!.Contains("Hma"));

        o = Baseline(); o.Risk.StopAtrMultiplierMin = 3m;
        Assert.True(OptimizerParameterCatalog.Validate(o)!.Contains("StopAtrMultiplier"));

        o = Baseline(); o.Risk.RiskPercentPerTrade = 2m;
        Assert.True(OptimizerParameterCatalog.Validate(o)!.Contains("RiskPercentPerTrade"));

        o = Baseline(); o.Strategy.TrailingActiveTriggerR = 3.5m;
        Assert.True(OptimizerParameterCatalog.Validate(o)!.Contains("trailing"));

        o = Baseline(); o.MarketRegime.NormalThreshold = 85;
        Assert.True(OptimizerParameterCatalog.Validate(o)!.Contains("regime"));
    }

    // ---------------- grid expansion ----------------

    [Fact]
    public void Expand_SkipsInvariantBreakingCombinations_AndReportsThemInsteadOfRepairingThem()
    {
        var fast = OptimizerParameterCatalog.Find("HmaFastPeriod")!;
        var slow = OptimizerParameterCatalog.Find("HmaSlowPeriod")!;
        var grid = new List<GridParameter<BacktestEngineOptions>>
        {
            new(fast.Name, new[] { 9m, 25m }, fast.Apply),   // 25 >= every slow value tried below
            new(slow.Name, new[] { 18m, 21m }, slow.Apply),
        };
        var skipped = new List<string>();

        var combos = ParameterGridSearchEngine.Expand(grid, Baseline(), OptimizerParameterCatalog.Clone, OptimizerParameterCatalog.Validate, skipped);

        Assert.Equal(2, combos.Count);                 // (9,18) and (9,21)
        Assert.Equal(2, skipped.Count);                // (25,18) and (25,21)
        Assert.True(combos.All(c => c.Values["HmaFastPeriod"] == 9m));
        Assert.True(combos.All(c => c.Options.Indicator.HmaFastPeriod == 9));
    }

    // ---------------- ranking ----------------

    private static AntiOverfittingReport Ok() => new(false, Array.Empty<AntiOverfittingFlag>());
    private static AntiOverfittingReport Rejected() => new(true, new[] { new AntiOverfittingFlag("ExcessiveDrawdown", true, "x") });

    private static OptimizationCandidate Cand(decimal a, decimal b, decimal train, decimal validate, AntiOverfittingReport? report = null) =>
        new(new Dictionary<string, decimal> { ["A"] = a, ["B"] = b }, train, validate, 40, 15m, report ?? Ok());

    private static readonly IReadOnlyList<GridDimension> Dims = new[]
    {
        new GridDimension("A", new[] { 1m, 2m, 3m }), new GridDimension("B", new[] { 1m, 2m, 3m }),
    };

    [Fact]
    public void Ranking_PrefersAPlateau_OverALoneSpikeWithPoorNeighbors()
    {
        var cands = new List<OptimizationCandidate>();
        for (var a = 1; a <= 3; a++)
            for (var b = 1; b <= 3; b++)
            {
                decimal score = a <= 1 ? 30m : 5m;                 // a=1 column is a solid plateau (30)
                if (a == 3 && b == 3) score = 45m;                 // a lone spike (45) surrounded by 5s
                cands.Add(Cand(a, b, score, score));
            }

        var ranked = OptimizationRanker.Rank(cands, Dims);

        // Spike: 0.5*45 + 0.5*5 = 25. Plateau middle (a=1,b=2): neighbors 30,30,5 -> mean 21.67 -> 0.5*30+0.5*21.67 = 25.8 > spike.
        var winner = ranked[0].Candidate.Parameters;
        Assert.Equal(1m, winner["A"]);
        Assert.True(ranked.First(r => r.Candidate.Parameters["A"] == 3 && r.Candidate.Parameters["B"] == 3).Rank > 1, "a lone spike must not win");
    }

    [Fact]
    public void Ranking_ChargesTheTrainOverValidateGap_NotTheReverse()
    {
        var degraded = Cand(1, 1, train: 50m, validate: 20m);   // looked great in Train, worse in Validate
        var improved = Cand(3, 3, train: 10m, validate: 20m);   // Validate better than Train: no charge

        var ranked = OptimizationRanker.Rank(new[] { degraded, improved }, Dims);

        Assert.Equal(-10m, ranked.First(r => r.Candidate == degraded).SelectionScore); // 20 - (50 - 20)
        Assert.Equal(20m, ranked.First(r => r.Candidate == improved).SelectionScore);
    }

    [Fact]
    public void Ranking_RejectedCandidates_AlwaysRankAfterAcceptedOnes_EvenWithHigherScores()
    {
        var great = Cand(1, 1, 90m, 90m, Rejected());
        var meh = Cand(3, 3, 5m, 5m);

        var ranked = OptimizationRanker.Rank(new[] { great, meh }, Dims);

        Assert.Equal(meh, ranked[0].Candidate);
        Assert.True(ranked[1].Rejected);
    }

    [Fact]
    public void Ranking_IsolatedCandidate_KeepsItsOwnScore_AndReportsNoNeighbors()
    {
        var ranked = OptimizationRanker.Rank(new[] { Cand(1, 1, 12m, 12m), Cand(3, 3, 8m, 8m) }, Dims); // not adjacent (A and B both differ by 2 steps)

        Assert.Equal(0, ranked[0].NeighborCount);
        Assert.Equal(12m, ranked[0].RobustScore);
        Assert.True(ranked[0].NeighborMeanScore is null);
    }

    [Fact]
    public void Ranking_RejectsAnOutOfRangeOwnWeight()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OptimizationRanker.Rank(new[] { Cand(1, 1, 1m, 1m) }, Dims, 1.5m));
    }

    // ---------------- evaluator ----------------

    private static BacktestRunResult Result(decimal cagr, decimal maxDd, int trades)
    {
        var list = Enumerable.Range(0, trades).Select(i => SelectionFixture.MakeTrade(i % 2 == 0 ? "AAA" : "BBB", 500m)).ToList();
        return SelectionFixture.MakeResult(new DateOnly(2022, 1, 1), new DateOnly(2023, 1, 1), list, cagr, sharpe: 1m, maxDrawdownPercent: maxDd);
    }

    [Fact]
    public void Evaluator_RejectsExcessiveDrawdown_AndTrainPositiveValidateNegative()
    {
        var d = new DateOnly(2022, 1, 1);
        var goodTrain = Result(30m, 10m, 40);

        var deepDd = OptimizationCandidateEvaluator.Evaluate(new Dictionary<string, decimal>(), goodTrain, Result(30m, 55m, 40), d, d, d, d, 40m, 30, 80);
        var collapsed = OptimizationCandidateEvaluator.Evaluate(new Dictionary<string, decimal>(), goodTrain, Result(-20m, 10m, 40), d, d, d, d, 40m, 30, 80);
        var fine = OptimizationCandidateEvaluator.Evaluate(new Dictionary<string, decimal>(), goodTrain, Result(25m, 12m, 40), d, d, d, d, 40m, 30, 80);

        Assert.True(deepDd.Overfitting.Rejected);
        Assert.True(collapsed.Overfitting.Rejected);
        Assert.False(fine.Overfitting.Rejected);
        Assert.Equal(40, fine.ValidateTradeCount);
    }

    [Fact]
    public void ToRecord_StoresParameters_Breakdown_AndMarksValidateAsNotOutOfSample()
    {
        var ranked = OptimizationRanker.Rank(new[] { Cand(1, 1, 10m, 12m), Cand(2, 1, 11m, 14m) }, Dims);

        var record = OptimizeCommand.ToRecord(ranked[0]);

        using var doc = JsonDocument.Parse(record.CompositeScoreBreakdownJson);
        Assert.Equal(1, doc.RootElement.GetProperty("rank").GetInt32());
        Assert.False(record.IsOutOfSample);
        Assert.Equal(ranked[0].RobustScore, record.CompositeScore);
        Assert.True(JsonDocument.Parse(record.ParameterSetJson).RootElement.TryGetProperty("A", out _));
    }

    // ---------------- CLI grammar ----------------

    [Fact]
    public void Arguments_ParseRepeatedParams_SortAndDeduplicateValues()
    {
        var r = OptimizeArguments.Parse(new[] { "optimize", "--param", "BreakoutMinAdx=25,15,20,15", "--param", "riskpercentpertrade=0.5,1", "--top", "5", "--max-combinations", "50" });

        Assert.Null(r.Error);
        Assert.Equal(2, r.Parameters.Count);
        Assert.True(r.Parameters[0].Values.SequenceEqual(new[] { 15m, 20m, 25m }));
        Assert.Equal("RiskPercentPerTrade", r.Parameters[1].Name); // canonical casing from the catalog
        Assert.Equal(5, r.Top);
        Assert.Equal(50, r.MaxCombinations);
    }

    [Fact]
    public void Arguments_RejectEveryMalformedForm_WithAClearMessage_NeverSilentlyIgnoring()
    {
        string? Err(params string[] a) => OptimizeArguments.Parse(new[] { "optimize" }.Concat(a).ToArray()).Error;

        Assert.True(Err("--param", "NoSuchThing=1,2")!.Contains("Unknown parameter"));
        Assert.True(Err("--param", "BreakoutMinAdx=abc")!.Contains("not a number"));
        Assert.True(Err("--param", "BreakoutMinAdx")!.Contains("Name=v1"));
        Assert.True(Err("--param", "BreakoutMinAdx=")!.Contains("Name=v1"));
        Assert.True(Err("--param")!.Contains("needs a value"));
        Assert.True(Err("--param", "BreakoutMinAdx=20", "--param", "BreakoutMinAdx=25")!.Contains("more than once"));
        Assert.True(Err("--top", "0")!.Contains("positive"));
        Assert.True(Err("--max-combinations", "x")!.Contains("positive"));
    }

    [Fact]
    public void Arguments_UseInvariantCulture_ForDecimalValues()
    {
        var r = OptimizeArguments.Parse(new[] { "optimize", "--param", "RiskPercentPerTrade=0.75,1.25" });

        Assert.True(r.Parameters[0].Values.SequenceEqual(new[] { 0.75m, 1.25m }));
    }
}
