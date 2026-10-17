using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Execution.Validation;

namespace TradingPlatform.Execution.OrderManagement;

/// <summary>Full outcome of one OrderManager.SubmitAsync call — Events is the complete state-transition audit trail (spec §7.2: "immutable append-only log of every order state transition"), regardless of whether the order was ultimately accepted.</summary>
public sealed record SubmitOrderOutcome(Order Order, bool Accepted, OrderValidationResult Validation, IReadOnlyList<OrderEvent> Events);

/// <summary>
/// Implements spec §6.3's order flow: "Signal → Risk Engine → Portfolio
/// Constraint Engine → Order Validation → Order Manager → [Broker]
/// Adapter → Broker → Order Update → Reconciliation → SQLite." The Risk
/// Engine / Portfolio Constraint Engine steps are Tranches 8/10's
/// PositionSizingCalculator / PortfolioRiskGate — by the time an order
/// reaches OrderManager, sizing is already decided; this class's own job
/// is strictly the last three steps of that chain: validate, respect the
/// kill switch, submit, and record every transition.
///
/// Deliberately provider-agnostic — takes an IExecutionProvider, so the
/// exact same OrderManager code path runs whether that provider is
/// BacktestFillSimulator's adapter, PaperExecutionProvider, or
/// ZerodhaOrderExecutionService (spec §1.2's shared-logic seam, extended
/// one layer up from where StrategyStepEngine already guarantees it).
/// </summary>
public sealed class OrderManager
{
    private readonly IExecutionProvider _executionProvider;

    public OrderManager(IExecutionProvider executionProvider) => _executionProvider = executionProvider;

    public async Task<SubmitOrderOutcome> SubmitAsync(
        Order candidateOrder,
        OrderValidationInputs validationInputs,
        KillSwitchState killSwitchState,
        CancellationToken cancellationToken = default)
    {
        var events = new List<OrderEvent>();
        var now = DateTimeOffset.UtcNow;

        void Transition(OrderState from, OrderState to, string? detail)
        {
            events.Add(new OrderEvent { OrderId = candidateOrder.Id, FromState = from, ToState = to, Detail = detail, Timestamp = DateTimeOffset.UtcNow });
            candidateOrder.State = to;
            candidateOrder.UpdatedAt = DateTimeOffset.UtcNow;
        }

        candidateOrder.State = OrderState.Created;
        candidateOrder.CreatedAt = candidateOrder.CreatedAt == default ? now : candidateOrder.CreatedAt;

        var validation = OrderValidationEngine.Validate(validationInputs);
        if (!validation.IsValid)
        {
            candidateOrder.RejectionReason = validation.RejectionReason;
            Transition(OrderState.Created, OrderState.Rejected, validation.RejectionReason);
            return new SubmitOrderOutcome(candidateOrder, false, validation, events);
        }
        Transition(OrderState.Created, OrderState.Validated, "Passed pre-trade validation.");

        if (!KillSwitchEngine.AllowsOrder(killSwitchState, candidateOrder.Side))
        {
            var reason = $"Kill switch active (source: {killSwitchState.Source}: {killSwitchState.Reason}).";
            candidateOrder.RejectionReason = reason;
            Transition(OrderState.Validated, OrderState.Rejected, reason);
            return new SubmitOrderOutcome(candidateOrder, false, validation, events);
        }

        var fromState = candidateOrder.State;
        var submitted = await _executionProvider.SubmitOrderAsync(candidateOrder, cancellationToken).ConfigureAwait(false);
        events.Add(new OrderEvent { OrderId = submitted.Id, FromState = fromState, ToState = submitted.State, Detail = submitted.RejectionReason, Timestamp = DateTimeOffset.UtcNow });

        var accepted = submitted.State is not (OrderState.Rejected or OrderState.Failed);
        return new SubmitOrderOutcome(submitted, accepted, validation, events);
    }
}
