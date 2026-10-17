using TradingPlatform.Backtesting.Execution;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Paper;

/// <summary>One symbol's current market snapshot, as needed to simulate a paper fill.</summary>
public sealed record PaperQuote(HistoricalBar Bar, decimal AverageDailyTradedValue, MarketCapBucket MarketCapBucket);

/// <summary>Live-data seam for paper trading — a real implementation would poll Kite's quote API or the most recent daily bar; kept abstract here so PaperExecutionProvider is testable without either.</summary>
public interface IQuoteSource
{
    Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);
}

/// <summary>
/// Paper trading provider (spec §1.2/§6.1's TradingMode.Paper): the same
/// StrategyStepEngine decision logic runs against the same
/// IExecutionProvider seam as Backtest and Live, but fills are simulated
/// (never a real broker order) against a live/near-live quote instead of
/// historical bars. Deliberately REUSES BacktestFillSimulator verbatim
/// (Tranche 11) rather than a second, parallel fill-simulation
/// implementation — a paper fill against "today's quote treated as one
/// bar" is structurally identical to a backtest fill against one
/// historical bar, and having two separately-maintained slippage/
/// liquidity/order-type implementations would be a correctness risk for
/// no benefit (spec §1.2's entire premise: backtest, paper, and live
/// share verified logic, differing only at THIS seam).
///
/// Order state is kept in-memory (spec §7.2's actual OrderEvent/Fill
/// persistence is an Application/Data-layer wiring concern for whichever
/// tranche builds the live-trading host process) — this class's job is
/// the simulation semantics, not durability.
/// </summary>
public sealed class PaperExecutionProvider : IExecutionProvider
{
    private readonly IQuoteSource _quoteSource;
    private readonly BacktestOptions _backtestOptions;
    private readonly RiskOptions _riskOptions;
    private readonly Dictionary<long, Order> _orders = new();
    private long _nextOrderId = 1;

    public PaperExecutionProvider(IQuoteSource quoteSource, BacktestOptions backtestOptions, RiskOptions riskOptions)
    {
        _quoteSource = quoteSource;
        _backtestOptions = backtestOptions;
        _riskOptions = riskOptions;
    }

    public async Task<Order> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        if (order.Id == 0) order.Id = _nextOrderId++;
        order.CreatedAt = order.CreatedAt == default ? DateTimeOffset.UtcNow : order.CreatedAt;

        var quote = await _quoteSource.GetQuoteAsync(order.Symbol, cancellationToken).ConfigureAwait(false);
        var fill = BacktestFillSimulator.Simulate(order, quote.Bar, quote.AverageDailyTradedValue, quote.MarketCapBucket, _backtestOptions, _riskOptions);

        order.State = fill switch
        {
            { Filled: true, FilledQuantity: var q } when q < order.Quantity => OrderState.PartiallyFilled,
            { Filled: true } => OrderState.Filled,
            _ => OrderState.Open, // resting — a Limit/Stop that hasn't triggered yet; the caller may resubmit/poll on a later quote
        };
        order.UpdatedAt = DateTimeOffset.UtcNow;
        if (!fill.Filled) order.RejectionReason = fill.Reason;
        else
        {
            order.FilledQuantity = fill.FilledQuantity;
            order.AverageFillPrice = fill.ExecutionPrice;
            order.FillSlippageAmount = fill.SlippageAmount;
            order.FillCommission = fill.TransactionCosts?.Total ?? 0m;
        }

        _orders[order.Id] = order;
        return order;
    }

    public Task<Order> CancelOrderAsync(long orderId, CancellationToken cancellationToken = default)
    {
        if (!_orders.TryGetValue(orderId, out var order))
            throw new InvalidOperationException($"No paper order with id {orderId}.");

        if (order.State is OrderState.Filled or OrderState.PartiallyFilled)
            throw new InvalidOperationException($"Order {orderId} is already {order.State} and cannot be cancelled.");

        order.State = OrderState.Cancelled;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(order);
    }

    public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.State is OrderState.Open or OrderState.PartiallyFilled).ToList());
}
