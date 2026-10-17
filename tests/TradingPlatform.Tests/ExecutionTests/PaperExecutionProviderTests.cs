using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Paper;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class PaperExecutionProviderTests
{
    private static readonly BacktestOptions BacktestOptions = new() { SlippageModel = SlippageModelType.FixedBps, FixedSlippageBps = 10m };
    private static readonly RiskOptions RiskOptions = new() { MaxParticipationPercentOfAdtv = 2m };

    private sealed class FakeQuoteSource : IQuoteSource
    {
        private readonly HistoricalBar _bar;
        public FakeQuoteSource(HistoricalBar bar) => _bar = bar;
        public Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaperQuote(_bar, 10_000_000m, MarketCapBucket.Mid));
    }

    private static HistoricalBar Bar(decimal open, decimal high, decimal low, decimal close, long volume) => new()
    {
        Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow, Open = open, High = high, Low = low, Close = close,
        AdjustedClose = close, Volume = volume, Source = "Fixture", Timeframe = Timeframe.Daily,
    };

    [Fact]
    public async Task SubmitOrderAsync_MarketOrder_FillsImmediately()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 102m, 99m, 101m, 1_000_000)), BacktestOptions, RiskOptions);
        var order = new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 100m, CorrelationId = "c1" };

        var result = await provider.SubmitOrderAsync(order);

        Assert.Equal(OrderState.Filled, result.State);
        Assert.True(result.Id > 0);
    }

    [Fact]
    public async Task SubmitOrderAsync_LimitOrderNotTouched_StaysOpen()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 101m, 99m, 100m, 1_000_000)), BacktestOptions, RiskOptions);
        var order = new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 100m, LimitPrice = 90m, CorrelationId = "c1" };

        var result = await provider.SubmitOrderAsync(order);

        Assert.Equal(OrderState.Open, result.State);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task SubmitOrderAsync_LiquidityCapped_PartiallyFills()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 102m, 99m, 101m, 10_000)), BacktestOptions, RiskOptions); // 2% of 10,000 = 200
        var order = new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1_000m, CorrelationId = "c1" };

        var result = await provider.SubmitOrderAsync(order);

        Assert.Equal(OrderState.PartiallyFilled, result.State);
    }

    [Fact]
    public async Task CancelOrderAsync_OpenOrder_Cancels()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 101m, 99m, 100m, 1_000_000)), BacktestOptions, RiskOptions);
        var order = new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 100m, LimitPrice = 90m, CorrelationId = "c1" };
        var submitted = await provider.SubmitOrderAsync(order);

        var cancelled = await provider.CancelOrderAsync(submitted.Id);

        Assert.Equal(OrderState.Cancelled, cancelled.State);
    }

    [Fact]
    public async Task CancelOrderAsync_AlreadyFilled_Throws()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 102m, 99m, 101m, 1_000_000)), BacktestOptions, RiskOptions);
        var order = new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 100m, CorrelationId = "c1" };
        var submitted = await provider.SubmitOrderAsync(order);

        try
        {
            await provider.CancelOrderAsync(submitted.Id);
            Assert.True(false, "Expected InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
            // expected
        }
    }

    [Fact]
    public async Task GetOpenOrdersAsync_ReturnsOnlyOpenAndPartiallyFilled()
    {
        var provider = new PaperExecutionProvider(new FakeQuoteSource(Bar(100m, 101m, 99m, 100m, 1_000_000)), BacktestOptions, RiskOptions);
        await provider.SubmitOrderAsync(new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 100m, CorrelationId = "c1" }); // fills
        await provider.SubmitOrderAsync(new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 100m, LimitPrice = 90m, CorrelationId = "c2" }); // stays open

        var openOrders = await provider.GetOpenOrdersAsync();

        Assert.Equal(1, openOrders.Count);
        Assert.Equal(OrderType.Limit, openOrders[0].Type);
    }
}
