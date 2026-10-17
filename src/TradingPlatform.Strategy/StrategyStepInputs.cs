using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Entry;

namespace TradingPlatform.Strategy;

/// <summary>
/// Inputs to StrategyStepEngine — the single shared decision function
/// spec §1.2/§5.6 call for: Backtest/Paper/Live must all reach an entry
/// decision through this exact same logic, differing only in which
/// IHistoricalMarketDataProvider/IExecutionProvider/IClock produced these
/// inputs. TotalTargetQuantity is accepted as an external, optional input
/// deliberately: position sizing is Tranche 8's (Risk) responsibility, not
/// this engine's — a signal can be evaluated and reported correctly even
/// before that wiring exists, just without a concrete share count attached.
/// </summary>
public sealed class StrategyStepInputs
{
    public required string Symbol { get; init; }

    /// <summary>Ascending, the stock's own OHLCV, up to and including "now".</summary>
    public required IReadOnlyList<HistoricalBar> Bars { get; init; }

    /// <summary>Ascending, index-aligned with Bars.</summary>
    public required IReadOnlyList<IndicatorSnapshot> IndicatorSnapshots { get; init; }

    public VcpPattern? VcpPattern { get; init; }
    public required decimal RelativeStrengthScore { get; init; }
    public required RegimeClassification Regime { get; init; }

    /// <summary>Null = no open position in this symbol (only a Stage 1 entry is possible).</summary>
    public StagedPositionState? ExistingPosition { get; init; }

    /// <summary>From Risk/position sizing (Tranche 8) — optional; when absent, a signal is still reported but without a concrete SuggestedQuantity.</summary>
    public decimal? TotalTargetQuantity { get; init; }

    /// <summary>Delivery % vs the stock's recent norm for the signal day (spec §3.4); consulted only if StrategyOptions.BreakoutMinDeliveryRatio is set.</summary>
    public DeliverySnapshot? Delivery { get; init; }
}
