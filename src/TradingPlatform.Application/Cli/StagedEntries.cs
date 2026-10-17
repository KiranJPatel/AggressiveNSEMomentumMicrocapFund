using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Entry;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Per-position pyramiding state for paper/live (spec §3.7: 40% / 30% / 30%). Stored as JSON in EngineState rather than
/// as new Positions columns — it is strategy bookkeeping, not broker truth, so it needs no schema migration and cannot
/// make a position row unreadable by an older build. Without it a restart could not know the total target size.
/// </summary>
public sealed record PositionStaging(
    decimal TotalTargetQuantity,
    decimal Stage1EntryPrice,
    DateTimeOffset Stage1EntryTimestamp,
    decimal? Stage2EntryPrice = null,
    DateTimeOffset? Stage2EntryTimestamp = null,
    int RegimeScoreAtEntry = 0,
    decimal StockScoreAtEntry = 0m,
    decimal RiskAtEntryPercent = 0m,
    decimal EntryCommission = 0m)   // running total of entry-leg commission still attributable to the open position
{
    public static string StateKey(string symbol) => $"PositionStaging:{symbol}";
}

/// <summary>
/// Builds the OrderIntent for each kind of order, carrying the decision-time journal context (regime score, stock score,
/// risk %, MFE/MAE) that a later — possibly next-cycle, live — fill needs.
/// </summary>
public sealed class IntentFactory
{
    private readonly int _regimeScore; private readonly decimal _riskPercent;
    private readonly IReadOnlyDictionary<string, (decimal MfeR, decimal MaeR)> _excursions;

    public IntentFactory(int regimeScore, decimal riskPercentPerTrade, IReadOnlyDictionary<string, (decimal MfeR, decimal MaeR)> excursionsBySymbol)
    { _regimeScore = regimeScore; _riskPercent = riskPercentPerTrade; _excursions = excursionsBySymbol; }

    public OrderIntent Exit(Position p, ExitReason reason)
    {
        var (mfe, mae) = _excursions.TryGetValue(p.Symbol, out var e) ? e : (0m, 0m);
        return new("Exit", p.Symbol, 0m, p.AverageEntryPrice, p.CurrentStop, p.Quantity, reason, MfeR: mfe, MaeR: mae);
    }

    public OrderIntent Entry(ScannerResult c) =>
        new("Entry", c.Symbol, c.PivotPrice, c.SuggestedEntry, c.SuggestedStop, c.SuggestedPositionSize, null, PyramidStage.Stage1Initial,
            c.SuggestedPositionSize, _regimeScore, c.Score, _riskPercent);

    public OrderIntent Add(Position p, AddPlan plan) =>
        new("Add", p.Symbol, 0m, p.AverageEntryPrice, p.CurrentStop, plan.Quantity, null, plan.Stage);

    /// <summary>
    /// MFE/MAE in R, identical to the backtest definition: (highest high since entry - avg entry) / risk-per-share and
    /// (lowest low since entry - avg entry) / risk-per-share, with both extremes seeded at the entry price (MFE &gt;= 0, MAE &lt;= 0). Uses bars
    /// from the entry DATE onward; with no usable bars both are 0 rather than invented.
    /// </summary>
    public static (decimal MfeR, decimal MaeR) Excursions(Position p, IReadOnlyList<HistoricalBar> bars)
    {
        if (p.RiskPerShare <= 0) return (0m, 0m);
        var since = bars.Where(b => b.Timestamp.Date >= p.OpenedAt.Date).ToList();
        if (since.Count == 0) return (0m, 0m);
        // Extremes start AT the entry price (as the backtest's per-lot tracking does), so MFE >= 0 and MAE <= 0 by construction.
        var highest = Math.Max(p.AverageEntryPrice, since.Max(b => b.High));
        var lowest = Math.Min(p.AverageEntryPrice, since.Min(b => b.Low));
        return ((highest - p.AverageEntryPrice) / p.RiskPerShare, (lowest - p.AverageEntryPrice) / p.RiskPerShare);
    }
}

public sealed record AddPlan(PyramidStage Stage, decimal Quantity, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides whether an open position should add its next pyramid stage THIS cycle. Reuses the same trigger evaluators the
/// backtest's StrategyStepEngine uses (Stage2/Stage3TriggerEvaluator) and the same predetermined quantity split, so
/// paper/live and backtest apply identical rules. Pure — no I/O.
/// </summary>
public static class StagedAddPlanner
{
    public static AddPlan? Plan(Position position, PositionStaging staging, IReadOnlyList<HistoricalBar> bars, IndicatorSnapshot lastIndicators, StrategyOptions options)
    {
        if (bars.Count == 0 || staging.TotalTargetQuantity <= 0) return null;
        if (position.HighestStageFilled is not (PyramidStage.Stage1Initial or PyramidStage.Stage2Add)) return null;

        var state = new StagedPositionState
        {
            Symbol = position.Symbol, HighestStageFilled = position.HighestStageFilled, CurrentStop = position.CurrentStop,
            Stage1EntryPrice = staging.Stage1EntryPrice, Stage1EntryTimestamp = staging.Stage1EntryTimestamp,
            Stage2EntryPrice = staging.Stage2EntryPrice, Stage2EntryTimestamp = staging.Stage2EntryTimestamp,
        };
        var split = StagedEntryCalculator.Split(staging.TotalTargetQuantity, options);

        if (position.HighestStageFilled == PyramidStage.Stage1Initial)
        {
            var eval = Stage2TriggerEvaluator.Evaluate(state, bars[^1], lastIndicators, options);
            return eval.ShouldTrigger && split.Stage2Qty > 0
                ? new AddPlan(PyramidStage.Stage2Add, split.Stage2Qty, eval.Conditions.Select(c => c.Detail).ToList()) : null;
        }

        var since = staging.Stage2EntryTimestamp is { } ts ? bars.Where(b => b.Timestamp >= ts).ToList() : new List<HistoricalBar>();
        var eval3 = Stage3TriggerEvaluator.Evaluate(state, since, lastIndicators, options);
        return eval3.ShouldTrigger && split.Stage3Qty > 0
            ? new AddPlan(PyramidStage.Stage3Add, split.Stage3Qty, eval3.Conditions.Select(c => c.Detail).ToList()) : null;
    }
}
