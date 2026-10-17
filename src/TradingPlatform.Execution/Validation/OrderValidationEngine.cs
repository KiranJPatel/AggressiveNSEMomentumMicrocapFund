using TradingPlatform.Core.Enums;

namespace TradingPlatform.Execution.Validation;

/// <summary>
/// Implements spec §6.3's pre-trade validation pipeline — the last gate
/// before an order reaches the broker: "market open, instrument valid,
/// symbol mapped, sufficient cash, position/sector/microcap limits,
/// portfolio heat, drawdown status, daily loss limit, order quantity,
/// liquidity, duplicate order, existing position, existing pending
/// order." Every check runs (not short-circuited) so a rejected order's
/// full picture is auditable, but IsValid is false if ANY applicable
/// check fails.
///
/// Six checks (cash, position/sector/microcap limits, portfolio heat,
/// drawdown/daily-loss-limit) apply ONLY to BUY orders — a SELL is by
/// definition risk-reducing in this long-only system (spec §2.2), and
/// spec §6.4's kill switch explicitly treats "risk-reducing exits" as the
/// one thing that should still be allowed even when everything else is
/// blocked. Gating a stop-loss exit behind "is there enough cash" or "is
/// trading shut down" would be exactly backwards — those checks exist to
/// stop taking on MORE risk, not to trap a position that needs to get
/// out.
/// </summary>
public static class OrderValidationEngine
{
    public static OrderValidationResult Validate(OrderValidationInputs inputs)
    {
        var checks = new List<OrderValidationCheck>
        {
            new("MarketOpen", inputs.IsMarketOpen, "Market is not currently open."),
            new("InstrumentActive", inputs.IsInstrumentActive, $"{inputs.CandidateOrder.Symbol} is not an active, tradeable instrument."),
            new("SymbolMapped", inputs.IsSymbolMapped, $"{inputs.CandidateOrder.Symbol} has no resolved broker/exchange symbol mapping."),
            new("OrderQuantityPositive", inputs.CandidateOrder.Quantity > 0, "Order quantity must be positive."),
            new("LiquidityCap", inputs.CandidateOrder.Quantity <= inputs.MaxFillableQuantity,
                $"Order quantity {inputs.CandidateOrder.Quantity} exceeds today's liquidity-based fillable cap of {inputs.MaxFillableQuantity}."),
            new("NotDuplicate", !inputs.IsDuplicateOrder, "An identical order was already submitted (duplicate)."),
            new("NoPendingOrderInSymbol", !inputs.HasPendingOrderInSymbol, $"An order for {inputs.CandidateOrder.Symbol} is already pending/working."),
            new("NoUnexpectedExistingPosition", !inputs.HasUnexpectedExistingPosition, $"An unexpected existing position in {inputs.CandidateOrder.Symbol} conflicts with this order."),
        };

        if (inputs.CandidateOrder.Side == OrderSide.Buy)
        {
            checks.Add(new("SufficientCash", inputs.EstimatedOrderValue <= inputs.AvailableCash,
                $"Estimated order value {inputs.EstimatedOrderValue:F2} exceeds available cash {inputs.AvailableCash:F2}."));
            checks.Add(new("PositionLimit", !inputs.WouldExceedPositionLimit, "This order would exceed the max single-position exposure limit."));
            checks.Add(new("SectorLimit", !inputs.WouldExceedSectorLimit, "This order would exceed the max sector exposure limit."));
            checks.Add(new("MicrocapLimit", !inputs.WouldExceedMicrocapLimit, "This order would exceed the max microcap exposure limit."));
            checks.Add(new("PortfolioHeat", inputs.ProjectedPortfolioHeatValue <= inputs.PortfolioHeatMaxValue,
                $"Projected portfolio heat {inputs.ProjectedPortfolioHeatValue:F2} would exceed the cap of {inputs.PortfolioHeatMaxValue:F2}."));
            checks.Add(new("DrawdownStatus", !inputs.TradingIsShutDown, "Trading is currently shut down (drawdown circuit breaker)."));
            checks.Add(new("DailyLossLimit", !inputs.DailyLossLimitBreached, "Today's daily loss limit has been breached — no new entries today."));
        }

        return new OrderValidationResult(checks.All(c => c.Passed), checks);
    }
}
