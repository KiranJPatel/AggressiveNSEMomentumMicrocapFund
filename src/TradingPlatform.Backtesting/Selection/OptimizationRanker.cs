using TradingPlatform.Backtesting.Engine;

namespace TradingPlatform.Backtesting.Selection;

/// <summary>One swept dimension with its values in ascending order — adjacency in this order defines "neighbor" for the robustness score.</summary>
public sealed record GridDimension(string Name, IReadOnlyList<decimal> Values);

/// <summary>One scored parameter combination.</summary>
public sealed record OptimizationCandidate(
    IReadOnlyDictionary<string, decimal> Parameters, decimal TrainScore, decimal ValidateScore,
    int ValidateTradeCount, decimal ValidateMaxDrawdownPercent, AntiOverfittingReport Overfitting);

public sealed record RankedOptimizationCandidate(
    int Rank, OptimizationCandidate Candidate, decimal SelectionScore, decimal RobustScore, decimal? NeighborMeanScore, int NeighborCount, bool Rejected);

/// <summary>
/// Spec §5.5: "Prefer a robust region of parameters over one perfect set", and selection must weigh out-of-sample behaviour, not the
/// in-sample peak. Three steps, all documented so a ranking is explainable rather than a black box:
///
///  1. SelectionScore = ValidateScore − max(0, TrainScore − ValidateScore). The Validate slice is the score; a candidate that looked
///     better in Train than Validate is charged the gap (the same out-of-sample-degradation term CompositeScoreCalculator uses for
///     walk-forward results).
///  2. RobustScore = 0.5 × own + 0.5 × mean(SelectionScore of grid neighbors) — neighbors differ in exactly one dimension by one grid
///     step. A lone spike surrounded by poor neighbors is pulled down toward them; a plateau keeps its score. A candidate with no
///     neighbors (single-combination grid, or sweeps of one value per dimension) keeps its own score.
///  3. A candidate the anti-overfitting check rejects (drawdown above the ceiling, or Train positive but Validate not) can never be the
///     winner: rejected candidates rank after every accepted one regardless of score.
/// </summary>
public static class OptimizationRanker
{
    public static IReadOnlyList<RankedOptimizationCandidate> Rank(
        IReadOnlyList<OptimizationCandidate> candidates, IReadOnlyList<GridDimension> dimensions, decimal ownWeight = 0.5m)
    {
        if (ownWeight is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(ownWeight), "must be within [0, 1]");

        var selection = candidates.Select(c => c.ValidateScore - Math.Max(0m, c.TrainScore - c.ValidateScore)).ToList();
        var scored = new List<(OptimizationCandidate C, decimal Selection, decimal Robust, decimal? NeighborMean, int NeighborCount)>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var neighborScores = new List<decimal>();
            for (var j = 0; j < candidates.Count; j++)
                if (i != j && AreNeighbors(candidates[i].Parameters, candidates[j].Parameters, dimensions)) neighborScores.Add(selection[j]);

            decimal? neighborMean = neighborScores.Count > 0 ? neighborScores.Average() : null;
            var robust = neighborMean is { } m ? ownWeight * selection[i] + (1m - ownWeight) * m : selection[i];
            scored.Add((candidates[i], selection[i], robust, neighborMean, neighborScores.Count));
        }

        return scored
            .OrderBy(s => s.C.Overfitting.Rejected)
            .ThenByDescending(s => s.Robust)
            .ThenByDescending(s => s.Selection)
            .Select((s, idx) => new RankedOptimizationCandidate(idx + 1, s.C, s.Selection, s.Robust, s.NeighborMean, s.NeighborCount, s.C.Overfitting.Rejected))
            .ToList();
    }

    private static bool AreNeighbors(IReadOnlyDictionary<string, decimal> a, IReadOnlyDictionary<string, decimal> b, IReadOnlyList<GridDimension> dimensions)
    {
        var differing = 0;
        foreach (var d in dimensions)
        {
            var va = a[d.Name];
            var vb = b[d.Name];
            if (va == vb) continue;
            var ia = IndexOf(d.Values, va);
            var ib = IndexOf(d.Values, vb);
            if (ia < 0 || ib < 0 || Math.Abs(ia - ib) != 1) return false;
            if (++differing > 1) return false;
        }
        return differing == 1;
    }

    private static int IndexOf(IReadOnlyList<decimal> values, decimal v)
    {
        for (var i = 0; i < values.Count; i++) if (values[i] == v) return i;
        return -1;
    }
}

/// <summary>Scores one parameter combination on a Train/Validate pair and attaches the anti-overfitting verdict (spec §5.5 + §5.7).</summary>
public static class OptimizationCandidateEvaluator
{
    public static OptimizationCandidate Evaluate(
        IReadOnlyDictionary<string, decimal> parameters, BacktestRunResult train, BacktestRunResult validate,
        DateOnly trainStart, DateOnly trainEnd, DateOnly validateStart, DateOnly validateEnd,
        decimal maxAcceptableDrawdownPercent, int minimumTradeCount, int aggressiveRegimeScoreThreshold)
    {
        var trainScore = CompositeScoreCalculator.CalculateSingleRun(train.Run, minimumTradeCount);
        var validateScore = CompositeScoreCalculator.CalculateSingleRun(validate.Run, minimumTradeCount);

        // One Train/Validate pair expressed as a single walk-forward window so the checker's "Train looked good, Test did not" rule applies.
        var window = new WalkForward.WalkForwardWindow(trainStart, trainEnd, validateStart, validateStart, validateStart, validateEnd);
        var report = AntiOverfittingChecker.Evaluate(
            validate, new[] { new WalkForward.WalkForwardWindowResult(window, train, validate, validate) },
            validate.Trades.GroupBy(t => t.Symbol).ToDictionary(g => g.Key, g => g.Count()),
            maxAcceptableDrawdownPercent, minimumTradeCount, aggressiveRegimeScoreThreshold);

        return new OptimizationCandidate(parameters, trainScore, validateScore, validate.Run.TradeCount ?? 0, validate.Run.MaxDrawdownPercent ?? 100m, report);
    }
}
