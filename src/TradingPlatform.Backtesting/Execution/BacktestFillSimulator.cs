using TradingPlatform.Backtesting.Costs;
using TradingPlatform.Backtesting.Slippage;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Execution;

/// <summary>Filled is false when the order never triggered against the bar's range (a limit never touched, a stop never crossed) or there was zero liquidity to fill against — never a fabricated fill.</summary>
public sealed record FillSimulationResult(
    bool Filled,
    decimal FilledQuantity,
    decimal? ExecutionPrice,
    decimal SlippageAmount,
    TransactionCostBreakdown? TransactionCosts,
    string Reason);

/// <summary>
/// Implements spec §5.4's execution model for Market/Limit/Stop/StopLoss
/// orders against one day's RAW OHLC bar — never AdjustedClose (spec §5.3:
/// "raw OHLC is used for stop-loss/execution simulation ... never use
/// adjusted close"). A pure, synchronous function of (order, bar, market
/// context) -> fill outcome, deliberately NOT an IExecutionProvider
/// implementation itself: that interface's async, DI-facing shape belongs
/// to whichever Application-layer orchestrator actually wires Backtest/
/// Paper/Live together (a later tranche); this simulator is the
/// deterministic economics a thin IExecutionProvider adapter would wrap.
///
/// Order-type handling:
///   - Market: always triggers, fills at the bar's Open (spec §3.8's
///     NextOpen convention — the order becomes eligible for the NEXT
///     session's open, so "the bar" passed in here must already BE that
///     next session's bar, not the signal day's).
///   - Limit: triggers only if the bar's range actually touched the limit
///     (Buy: Low &lt;= Limit; Sell: High &gt;= Limit); fills AT the limit
///     price or better — slippage can never push a limit fill past its
///     own limit (a resolved simplification: real limit orders can still
///     get worse fills from queue priority, but "no worse than the limit"
///     is the defining guarantee of the order type and the safer
///     assumption for a backtest not to violate).
///   - Stop / StopLoss: triggers when the bar's range crosses the stop
///     (Buy: High &gt;= Stop; Sell: Low &lt;= Stop). Reference price
///     handles GAP-THROUGH explicitly: if the bar's Open already gapped
///     past the stop (e.g. a sell-stop with the open below the stop
///     level), the fill references the WORSE of (Stop, Open) — a real
///     stop order becomes a market order the instant it triggers and
///     cannot fill at a stale price the market never actually traded at
///     that day. This is a resolved ambiguity worth naming explicitly:
///     omitting it would silently understate loss on exactly the gap-down
///     days where correct simulation matters most.
///
/// Liquidity (spec §2.4: "Backtester must reject fills that historical
/// liquidity couldn't have actually supported"): the fillable quantity is
/// capped at RiskOptions.MaxParticipationPercentOfAdtv of the BAR'S OWN
/// actual volume that day — deliberately the same participation-cap
/// mechanism PositionSizingCalculator already applies against the rolling
/// ADTV at sizing time (Tranche 8), reapplied here against the specific
/// day's real number, which can differ sharply from the rolling average.
/// A request that exceeds this becomes a PARTIAL fill (spec §5.1/§5.4
/// explicitly support partial fills), never silently filled in full.
/// </summary>
public static class BacktestFillSimulator
{
    public static FillSimulationResult Simulate(
        Order order,
        HistoricalBar executionBar,
        decimal averageDailyTradedValue,
        MarketCapBucket marketCapBucket,
        BacktestOptions backtestOptions,
        RiskOptions riskOptions)
    {
        if (order.Quantity <= 0) return NoFill("Order quantity must be positive.");

        var (referencePrice, triggered) = order.Type switch
        {
            OrderType.Market => (executionBar.Open, true),

            OrderType.Limit => (
                order.LimitPrice ?? throw new ArgumentException("Limit order requires LimitPrice.", nameof(order)),
                order.Side == OrderSide.Buy ? executionBar.Low <= order.LimitPrice!.Value : executionBar.High >= order.LimitPrice!.Value),

            OrderType.Stop or OrderType.StopLoss => StopReferenceAndTrigger(order, executionBar),

            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unsupported order type."),
        };

        if (!triggered)
            return NoFill($"{order.Type} order ({order.Side}, level {referencePrice}) was not touched by the bar's range ({executionBar.Low}-{executionBar.High}).");

        var maxFillableQty = Math.Floor(executionBar.Volume * riskOptions.MaxParticipationPercentOfAdtv / 100m);
        if (maxFillableQty <= 0)
            return NoFill("Zero (or negligible) volume bar — no liquidity to fill against.");

        var filledQuantity = Math.Min(order.Quantity, maxFillableQty);
        var partial = filledQuantity < order.Quantity;

        var slippage = SlippageCalculator.Calculate(referencePrice, order.Side, filledQuantity, executionBar, averageDailyTradedValue, marketCapBucket, backtestOptions);

        var executionPrice = order.Type == OrderType.Limit
            ? ClampToLimit(slippage.ExecutionPrice, order.Side, order.LimitPrice!.Value)
            : slippage.ExecutionPrice;

        var turnoverValue = executionPrice * filledQuantity;
        var costs = TransactionCostCalculator.Calculate(turnoverValue, order.Side, backtestOptions.Commission);
        var slippageAmount = Math.Abs(executionPrice - referencePrice) * filledQuantity;

        var reason = partial
            ? $"Partial fill: {filledQuantity}/{order.Quantity} — day's volume ({executionBar.Volume}) at {riskOptions.MaxParticipationPercentOfAdtv}% participation cap only supports {maxFillableQty}."
            : "Filled in full.";

        return new FillSimulationResult(true, filledQuantity, executionPrice, slippageAmount, costs, reason);
    }

    private static (decimal ReferencePrice, bool Triggered) StopReferenceAndTrigger(Order order, HistoricalBar bar)
    {
        var stopPrice = order.StopPrice ?? throw new ArgumentException("Stop/StopLoss order requires StopPrice.", nameof(order));

        if (order.Side == OrderSide.Sell)
        {
            var triggered = bar.Low <= stopPrice;
            var reference = Math.Min(stopPrice, bar.Open); // gapped through -> no better than the (worse, lower) open
            return (reference, triggered);
        }
        else
        {
            var triggered = bar.High >= stopPrice;
            var reference = Math.Max(stopPrice, bar.Open); // gapped through -> no better than the (worse, higher) open
            return (reference, triggered);
        }
    }

    private static decimal ClampToLimit(decimal slippedPrice, OrderSide side, decimal limitPrice) =>
        side == OrderSide.Buy ? Math.Min(slippedPrice, limitPrice) : Math.Max(slippedPrice, limitPrice);

    private static FillSimulationResult NoFill(string reason) => new(false, 0m, null, 0m, null, reason);
}
