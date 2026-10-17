using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Validation;

/// <summary>
/// Every fact OrderValidationEngine.Validate needs, pre-resolved by the
/// caller (the Order Manager) — this engine stays pure and I/O-free, the
/// same "caller aggregates portfolio/market state, engine just judges it"
/// split already used throughout Risk/Strategy (PositionSizingInputs,
/// StrategyStepInputs, etc.). None of these fields are derived here; all
/// are facts the Order Manager already had to gather to get this far.
/// </summary>
public sealed record OrderValidationInputs
{
    public required Order CandidateOrder { get; init; }

    public required bool IsMarketOpen { get; init; }
    public required bool IsInstrumentActive { get; init; }
    public required bool IsSymbolMapped { get; init; }

    /// <summary>BUY-only checks: cash, position/sector/microcap limits, portfolio heat, drawdown/loss-limit status. A risk-REDUCING sell must never be blocked by any of these (spec §6.4's kill switch explicitly carves out "risk-reducing exits only" as the one thing that may stay allowed).</summary>
    public required decimal AvailableCash { get; init; }
    public required decimal EstimatedOrderValue { get; init; }
    public required bool WouldExceedPositionLimit { get; init; }
    public required bool WouldExceedSectorLimit { get; init; }
    public required bool WouldExceedMicrocapLimit { get; init; }
    public required decimal ProjectedPortfolioHeatValue { get; init; }
    public required decimal PortfolioHeatMaxValue { get; init; }
    public required bool TradingIsShutDown { get; init; }
    public required bool DailyLossLimitBreached { get; init; }

    /// <summary>Applies to both sides — even an exit can't literally trade more than the day's liquidity supports.</summary>
    public required decimal MaxFillableQuantity { get; init; }

    public required bool IsDuplicateOrder { get; init; }
    public required bool HasUnexpectedExistingPosition { get; init; }
    public required bool HasPendingOrderInSymbol { get; init; }
}
