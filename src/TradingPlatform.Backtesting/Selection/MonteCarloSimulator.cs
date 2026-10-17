using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Selection;

public sealed record MonteCarloResult(
    decimal P5EndingCapital, decimal P25EndingCapital, decimal MedianEndingCapital, decimal P75EndingCapital, decimal P95EndingCapital,
    decimal ProbabilityDrawdownExceeds20Percent, decimal ProbabilityDrawdownExceeds30Percent, decimal ProbabilityDrawdownExceeds40Percent,
    decimal ProbabilityOfRuin, int IterationCount, MonteCarloMode Mode = MonteCarloMode.Reshuffle);

public enum MonteCarloMode
{
    /// <summary>Permutation of the same completed trades, rupee P&amp;L added in the new order. Answers "what if the same outcomes arrived in a different sequence" — drawdown/ruin only: the SUM of P&amp;L never changes, so ending capital varies only by the small slippage jitter.</summary>
    Reshuffle,

    /// <summary>Resampling WITH replacement of per-trade returns on equity (realized P&amp;L over the equity at entry), compounded on running capital. Answers "what if the next N trades were drawn from the same distribution" — the mode whose ending-capital percentiles are actually spread out.</summary>
    Bootstrap,
}

/// <summary>
/// Spec §5.5: "randomize trade order, win/loss sequence, slippage/
/// execution variation. Report 5th/25th/median/75th/95th percentile
/// ending capital, P(DD&gt;20/30/40%), probability of ruin."
///
/// Two resolved design points:
///
///   0. Two modes (MonteCarloMode). Reshuffle is the permutation described
///      next; Bootstrap was added because a permutation cannot spread ending
///      capital (the sum of P&L is invariant), so its percentiles say almost
///      nothing about outcome uncertainty — only about path risk.
///
///   1. In Reshuffle mode "trade order" and "win/loss sequence" are implemented as a single
///      PERMUTATION (Fisher-Yates shuffle) of the same fixed set of
///      completed trades — not bootstrap resampling WITH replacement.
///      Resampling-with-replacement is a common alternative Monte Carlo
///      variant, but it can duplicate or omit real trades, effectively
///      fabricating a trade history that never happened; a straight
///      reordering only ever asks "what if these same real outcomes had
///      arrived in a different sequence" — the safer, more conservative
///      reading of spec's wording, and the one this method implements.
///
///   2. "Slippage/execution variation" is implemented as a per-trade,
///      per-iteration random multiplicative jitter on RealizedPnl (a
///      small +/- percentage), NOT a full re-run of BacktestEngine with
///      randomized fill parameters — re-running the full simulation
///      thousands of times per Monte Carlo batch would be prohibitively
///      expensive and is not what "variation" on an already-completed
///      trade list can mean without re-simulating from raw bars. This is
///      the standard, widely-used pragmatic approximation for this kind
///      of path-risk analysis.
///
/// Deterministic given a seeded Random (spec §7.6: "every result must be
/// reproducible") — the caller controls reproducibility entirely through
/// which Random instance it passes in.
/// </summary>
public static class MonteCarloSimulator
{
    public static MonteCarloResult Simulate(
        IReadOnlyList<Trade> trades,
        decimal initialCapital,
        int iterations,
        Random random,
        decimal slippageJitterPercent = 5m,
        decimal ruinThresholdPercent = 50m,
        MonteCarloMode mode = MonteCarloMode.Reshuffle)
    {
        if (trades.Count == 0) throw new ArgumentException("At least one trade is required.", nameof(trades));
        if (iterations <= 0) throw new ArgumentException("iterations must be positive.", nameof(iterations));
        if (initialCapital <= 0) throw new ArgumentException("initialCapital must be positive.", nameof(initialCapital));

        var endingCapitals = new List<decimal>(iterations);
        var maxDrawdowns = new List<decimal>(iterations);
        var ruinCount = 0;
        var ruinFloor = initialCapital * (1m - ruinThresholdPercent / 100m);

        var pnls = trades.Select(t => t.RealizedPnl).ToArray();
        // Bootstrap works in returns on equity so a draw made late in a path scales with the capital then. Each trade's return is its REALIZED P&L
        // over the equity at the moment it was entered (starting capital plus the P&L of every trade already exited by then). This replaces an
        // earlier R-multiple x configured-risk-% formula that overstated returns whenever position sizing was capped below the configured risk —
        // on a real run it produced a median ending capital ~2.8x the backtest it was simulating. The new return reproduces actual sizing.
        var returns = EquityReturns(trades, initialCapital);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var shuffled = mode == MonteCarloMode.Reshuffle ? ShuffledCopy(pnls, random) : null;
            var capital = initialCapital;
            var peak = initialCapital;
            var maxDrawdownThisPath = 0m;
            var ruinedThisPath = false;

            for (var step = 0; step < pnls.Length; step++)
            {
                var jitter = 1m + ((decimal)random.NextDouble() * 2m - 1m) * slippageJitterPercent / 100m; // uniform in [1 - jitter%, 1 + jitter%]
                if (mode == MonteCarloMode.Reshuffle)
                    capital += shuffled![step] * jitter;
                else
                    capital = Math.Max(0m, capital * (1m + returns[random.Next(returns.Length)] * jitter)); // equity can fall to zero, never below

                if (capital > peak) peak = capital;
                var drawdown = peak > 0 ? (peak - capital) / peak * 100m : 0m;
                if (drawdown > maxDrawdownThisPath) maxDrawdownThisPath = drawdown;

                if (capital <= ruinFloor) ruinedThisPath = true;
            }

            endingCapitals.Add(capital);
            maxDrawdowns.Add(maxDrawdownThisPath);
            if (ruinedThisPath) ruinCount++;
        }

        endingCapitals.Sort();

        return new MonteCarloResult(
            P5EndingCapital: Percentile(endingCapitals, 5),
            P25EndingCapital: Percentile(endingCapitals, 25),
            MedianEndingCapital: Percentile(endingCapitals, 50),
            P75EndingCapital: Percentile(endingCapitals, 75),
            P95EndingCapital: Percentile(endingCapitals, 95),
            ProbabilityDrawdownExceeds20Percent: maxDrawdowns.Count(d => d > 20m) / (decimal)iterations * 100m,
            ProbabilityDrawdownExceeds30Percent: maxDrawdowns.Count(d => d > 30m) / (decimal)iterations * 100m,
            ProbabilityDrawdownExceeds40Percent: maxDrawdowns.Count(d => d > 40m) / (decimal)iterations * 100m,
            ProbabilityOfRuin: ruinCount / (decimal)iterations * 100m,
            IterationCount: iterations,
            Mode: mode);
    }

    private static decimal[] ShuffledCopy(decimal[] source, Random random)
    {
        var copy = (decimal[])source.Clone();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    /// <summary>Nearest-rank percentile against an already-sorted-ascending list.</summary>
    private static decimal Percentile(IReadOnlyList<decimal> sortedAscending, int percentile)
    {
        if (sortedAscending.Count == 1) return sortedAscending[0];
        var rank = (int)Math.Ceiling(percentile / 100m * sortedAscending.Count) - 1;
        rank = Math.Clamp(rank, 0, sortedAscending.Count - 1);
        return sortedAscending[rank];
    }

    /// <summary>Per-trade return on the equity at its entry: pnl / (initialCapital + P&amp;L of all trades exited at or before the entry). Equity at or below zero counts as a total loss (-100%).</summary>
    public static decimal[] EquityReturns(IReadOnlyList<Trade> trades, decimal initialCapital)
    {
        var byExit = trades.OrderBy(t => t.ExitAt).ToList();
        var cumulative = new decimal[byExit.Count + 1];                 // cumulative[k] = P&L of the first k exits
        for (var i = 0; i < byExit.Count; i++) cumulative[i + 1] = cumulative[i] + byExit[i].RealizedPnl;

        var result = new decimal[trades.Count];
        for (var i = 0; i < trades.Count; i++)
        {
            // number of trades whose exit is at or before this trade's entry (binary search over the exit-sorted list)
            int lo = 0, hi = byExit.Count;
            while (lo < hi) { var mid = (lo + hi) / 2; if (byExit[mid].ExitAt <= trades[i].EntryAt) lo = mid + 1; else hi = mid; }
            var equity = initialCapital + cumulative[lo];
            result[i] = equity > 0m ? trades[i].RealizedPnl / equity : -1m;
        }
        return result;
    }
}
