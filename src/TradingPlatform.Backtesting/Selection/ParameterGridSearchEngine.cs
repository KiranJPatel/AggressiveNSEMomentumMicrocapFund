namespace TradingPlatform.Backtesting.Selection;

/// <summary>
/// One swept dimension of the grid (spec §5.5 names: "RiskPercent, ATR
/// multiplier, RSI/ADX thresholds, volume multiplier, VCP score
/// threshold, breakout threshold, HMA periods, trailing-stop params,
/// regime thresholds"). Apply mutates whichever options object the
/// caller's TOptions actually is — the engine itself has zero knowledge
/// of which specific field each dimension touches, by design: hard-coding
/// ten different options classes' field names into this engine would
/// make it brittle against exactly the kind of options changes every
/// prior tranche has made repeatedly (e.g. Tranche 10/11 both added new
/// RiskOptions/BacktestOptions fields). The caller wires each dimension's
/// Apply against whichever concrete options type it's searching.
/// </summary>
public sealed record GridParameter<TOptions>(string Name, IReadOnlyList<decimal> Values, Action<TOptions, decimal> Apply);

public sealed record GridSearchResult<TOptions>(IReadOnlyDictionary<string, decimal> ParameterValues, TOptions Options, decimal CompositeScore);

/// <summary>
/// Spec §5.5's grid search. Cartesian-products every GridParameter's
/// Values, clones the baseline options for each combination (via
/// cloneOptions — the caller supplies this since only it knows the
/// concrete TOptions shape; a JSON round-trip is the simplest correct
/// implementation for the plain-POCO options classes this codebase uses),
/// applies every dimension's value to that clone, runs the caller's
/// scoreCombination delegate (typically a full WalkForwardEngine run plus
/// CompositeScoreCalculator.CalculateWalkForwardStability — see class
/// remarks on GridParameter for why grid search never optimizes on the
/// full dataset directly), and returns every combination ranked
/// best-first.
/// </summary>
public static class ParameterGridSearchEngine
{
    /// <summary>
    /// Expands the grid into one cloned, fully-applied option set per combination WITHOUT scoring anything — for callers that need richer
    /// per-candidate output than a single decimal (the optimize command scores Train and Validate separately). Combinations rejected by
    /// <paramref name="validate"/> (returns the broken invariant, or null) are skipped and reported in <paramref name="skipped"/> rather than repaired.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyDictionary<string, decimal> Values, TOptions Options)> Expand<TOptions>(
        IReadOnlyList<GridParameter<TOptions>> parameters, TOptions baselineOptions, Func<TOptions, TOptions> cloneOptions,
        Func<TOptions, string?>? validate = null, IList<string>? skipped = null)
    {
        if (parameters.Count == 0) throw new ArgumentException("At least one GridParameter is required.", nameof(parameters));
        if (parameters.Any(p => p.Values.Count == 0)) throw new ArgumentException("Every GridParameter must have at least one value.", nameof(parameters));

        var expanded = new List<(IReadOnlyDictionary<string, decimal>, TOptions)>();
        foreach (var combination in CartesianProduct(parameters))
        {
            var options = cloneOptions(baselineOptions);
            foreach (var (parameter, value) in combination) parameter.Apply(options, value);
            var values = combination.ToDictionary(c => c.Parameter.Name, c => c.Value);
            if (validate?.Invoke(options) is { } problem)
            {
                skipped?.Add($"{string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))}: {problem}");
                continue;
            }
            expanded.Add((values, options));
        }
        return expanded;
    }

    public static IReadOnlyList<GridSearchResult<TOptions>> Search<TOptions>(
        IReadOnlyList<GridParameter<TOptions>> parameters,
        TOptions baselineOptions,
        Func<TOptions, TOptions> cloneOptions,
        Func<TOptions, decimal> scoreCombination)
    {
        if (parameters.Count == 0) throw new ArgumentException("At least one GridParameter is required.", nameof(parameters));
        if (parameters.Any(p => p.Values.Count == 0)) throw new ArgumentException("Every GridParameter must have at least one value.", nameof(parameters));

        var combinations = CartesianProduct(parameters);
        var results = new List<GridSearchResult<TOptions>>(combinations.Count);

        foreach (var combination in combinations)
        {
            var options = cloneOptions(baselineOptions);
            foreach (var (parameter, value) in combination)
                parameter.Apply(options, value);

            var score = scoreCombination(options);
            var parameterValues = combination.ToDictionary(c => c.Parameter.Name, c => c.Value);
            results.Add(new GridSearchResult<TOptions>(parameterValues, options, score));
        }

        return results.OrderByDescending(r => r.CompositeScore).ToList();
    }

    private static List<List<(GridParameter<TOptions> Parameter, decimal Value)>> CartesianProduct<TOptions>(IReadOnlyList<GridParameter<TOptions>> parameters)
    {
        var result = new List<List<(GridParameter<TOptions>, decimal)>> { new() };
        foreach (var parameter in parameters)
        {
            var next = new List<List<(GridParameter<TOptions>, decimal)>>();
            foreach (var partial in result)
            foreach (var value in parameter.Values)
            {
                var extended = new List<(GridParameter<TOptions>, decimal)>(partial) { (parameter, value) };
                next.Add(extended);
            }
            result = next;
        }
        return result;
    }
}
