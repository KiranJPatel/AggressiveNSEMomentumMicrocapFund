using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.WalkForward;

namespace TradingPlatform.Backtesting.Selection;

public sealed record AntiOverfittingFlag(string Name, bool Triggered, string Detail);

/// <summary>Rejected mirrors spec §5.7's exact rule: "A candidate is rejected if MaxDD &gt; 40% or if attractive performance disappears out-of-sample." Every OTHER flag is a warning surfaced for a human to weigh, never an automatic rejection on its own — spec names them as things to "actively detect and flag," only two of which it explicitly makes disqualifying.</summary>
public sealed record AntiOverfittingReport(bool Rejected, IReadOnlyList<AntiOverfittingFlag> Flags);

/// <summary>
/// Spec §5.7: "Actively detect and flag: overfitting, too few trades,
/// parameter instability, excessive drawdown, single-stock/single-year/
/// bull-market dependence, survivorship bias, look-ahead bias."
///
/// LookAheadBias is deliberately NOT a computed statistical check here —
/// there is no aggregate-output statistic that can detect it after the
/// fact; it can only be prevented structurally, by construction, in how
/// every engine consumes data (bars.Take(i+1) slicing throughout Tranches
/// 3-11, verified by each engine's own no-look-ahead tests). This
/// method's LookAheadBiasStructurallyPrevented flag documents that
/// guarantee rather than "detecting" something a finished backtest's
/// numbers alone cannot reveal — reporting it as if it were a measured
/// check would overstate what a statistic can actually tell you.
///
/// BullMarketDependence uses each trade's RegimeScoreAtEntry (Trade
/// record, populated by BacktestEngine from the day's MarketRegimeSnapshot,
/// Tranche 11) — the share of trades entered while the regime score was
/// at/above an aggressive-regime threshold (an unambiguously bullish
/// backdrop). A strategy whose trades cluster almost entirely in the most
/// bullish regime band hasn't been tested through anything else.
/// </summary>
public static class AntiOverfittingChecker
{
    public static AntiOverfittingReport Evaluate(
        BacktestRunResult fullPeriodResult,
        IReadOnlyList<WalkForwardWindowResult>? walkForwardResults,
        IReadOnlyDictionary<string, int>? tradeCountBySymbol,
        decimal maxAcceptableDrawdownPercent,
        int minimumTradeCount,
        int aggressiveRegimeScoreThreshold,
        decimal maxSingleDependenceSharePercent = 50m)
    {
        var flags = new List<AntiOverfittingFlag>();
        var run = fullPeriodResult.Run;
        var tradeCount = run.TradeCount ?? 0;

        flags.Add(new("TooFewTrades", tradeCount < minimumTradeCount,
            $"Only {tradeCount} trades — below the {minimumTradeCount}-trade credibility floor (spec §5.7)."));

        var maxDrawdown = run.MaxDrawdownPercent ?? 0m;
        var excessiveDrawdown = maxDrawdown > maxAcceptableDrawdownPercent;
        flags.Add(new("ExcessiveDrawdown", excessiveDrawdown,
            $"MaxDrawdown {maxDrawdown:F1}% exceeds the {maxAcceptableDrawdownPercent:F0}% acceptance ceiling (spec §5.7/§8.2 — automatic rejection)."));

        flags.Add(new("SurvivorshipBiased", run.SurvivorshipBiased,
            "Backtest universe was not confirmed point-in-time (spec §5.6) — treat all results as optimistic."));

        flags.Add(new("LookAheadBiasStructurallyPrevented", false,
            "Not a computed statistic — prevented by construction via causal bars.Take(i+1) slicing throughout every engine (Tranches 3-11); see class remarks."));

        if (tradeCountBySymbol is { Count: > 0 })
        {
            var total = tradeCountBySymbol.Values.Sum();
            var maxShare = total > 0 ? tradeCountBySymbol.Values.Max() / (decimal)total * 100m : 0m;
            flags.Add(new("SingleStockDependence", maxShare > maxSingleDependenceSharePercent,
                $"One symbol accounts for {maxShare:F0}% of all trades (threshold {maxSingleDependenceSharePercent:F0}%)."));
        }

        var tradesByYear = fullPeriodResult.Trades.GroupBy(t => t.EntryAt.Year).ToDictionary(g => g.Key, g => g.Count());
        if (tradesByYear.Count > 0)
        {
            var totalTrades = tradesByYear.Values.Sum();
            var maxYearShare = totalTrades > 0 ? tradesByYear.Values.Max() / (decimal)totalTrades * 100m : 0m;
            flags.Add(new("SingleYearDependence", maxYearShare > maxSingleDependenceSharePercent,
                $"One calendar year accounts for {maxYearShare:F0}% of all trades (threshold {maxSingleDependenceSharePercent:F0}%)."));
        }

        if (fullPeriodResult.Trades.Count > 0)
        {
            var bullMarketShare = fullPeriodResult.Trades.Count(t => t.RegimeScoreAtEntry >= aggressiveRegimeScoreThreshold) / (decimal)fullPeriodResult.Trades.Count * 100m;
            flags.Add(new("BullMarketDependence", bullMarketShare > maxSingleDependenceSharePercent,
                $"{bullMarketShare:F0}% of trades were entered in an Aggressive-regime backdrop (threshold {maxSingleDependenceSharePercent:F0}%)."));
        }

        var outOfSampleDegraded = false;
        if (walkForwardResults is { Count: > 0 })
        {
            var trainScores = walkForwardResults.Select(w => CompositeScoreCalculator.CalculateSingleRun(w.TrainResult.Run, minimumTradeCount)).ToList();
            var testScores = walkForwardResults.Select(w => CompositeScoreCalculator.CalculateSingleRun(w.TestResult.Run, minimumTradeCount)).ToList();
            var avgTrain = trainScores.Average();
            var avgTest = testScores.Average();

            // "Attractive performance disappears out-of-sample": Train looked good (positive) but
            // Test does not — the specific disqualifying pattern spec §5.7 names, not just any gap.
            outOfSampleDegraded = avgTrain > 0m && avgTest <= 0m;
            flags.Add(new("OutOfSamplePerformanceDisappears", outOfSampleDegraded,
                $"Average Train composite score {avgTrain:F1} was positive but average Test score {avgTest:F1} was not (spec §5.7 — automatic rejection)."));

            var testStdDev = testScores.Count >= 2
                ? (decimal)Math.Sqrt((double)(testScores.Sum(s => (s - avgTest) * (s - avgTest)) / (testScores.Count - 1)))
                : 0m;
            var unstable = testScores.Count >= 2 && testStdDev > Math.Abs(avgTest) + 10m; // wide swing relative to the average itself
            flags.Add(new("ParameterInstability", unstable,
                $"Test-window composite scores vary widely across walk-forward windows (stddev {testStdDev:F1} vs average {avgTest:F1})."));
        }

        var rejected = excessiveDrawdown || outOfSampleDegraded;
        return new AntiOverfittingReport(rejected, flags);
    }
}
