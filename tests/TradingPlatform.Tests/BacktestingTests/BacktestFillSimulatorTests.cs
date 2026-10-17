using TradingPlatform.Backtesting.Execution;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.BacktestingTests.BacktestBarFixture;

namespace TradingPlatform.Tests.BacktestingTests;

public class BacktestFillSimulatorTests
{
    private static readonly BacktestOptions BacktestOptions = new()
    {
        SlippageModel = SlippageModelType.FixedBps,
        FixedSlippageBps = 10m, // 0.1%
    };

    private static readonly RiskOptions RiskOptions = new() { MaxParticipationPercentOfAdtv = 2m };

    private static Order MarketOrder(OrderSide side, decimal qty) => new()
    {
        Symbol = "TESTSYM",
        Side = side,
        Type = OrderType.Market,
        Quantity = qty,
        CorrelationId = "corr-1",
    };

    [Fact]
    public void MarketOrder_FillsAtBarOpen_PlusSlippage()
    {
        var bar = Bar(open: 100m, high: 102m, low: 99m, close: 101m, volume: 1_000_000);
        var order = MarketOrder(OrderSide.Buy, 100m);

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.Filled);
        Assert.Equal(100.10m, result.ExecutionPrice); // Open (100) + 10bps, never Close
    }

    [Fact]
    public void MarketOrder_UsesRawOpen_NeverAdjustedClose()
    {
        // AdjustedClose deliberately set far away from Open/Close to prove it's never consulted (spec §5.3).
        var bar = new HistoricalBar
        {
            Symbol = "TESTSYM", Timestamp = DateTimeOffset.UtcNow, Open = 100m, High = 102m, Low = 99m, Close = 101m,
            AdjustedClose = 50m, Volume = 1_000_000, Source = "Fixture", Timeframe = Timeframe.Daily,
        };
        var order = MarketOrder(OrderSide.Buy, 100m);

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.ExecutionPrice > 90m); // nowhere near the (unused) 50 AdjustedClose
    }

    [Fact]
    public void LimitBuy_Triggers_WhenBarLowTouchesLimit()
    {
        var bar = Bar(100m, 101m, 97m, 99m, 1_000_000);
        var order = new Order { Symbol = "TESTSYM", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 100m, LimitPrice = 98m, CorrelationId = "c" };

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.Filled);
        Assert.True(result.ExecutionPrice <= 98m, "A limit buy must never fill above its own limit.");
    }

    [Fact]
    public void LimitBuy_DoesNotTrigger_WhenBarNeverReachesLimit()
    {
        var bar = Bar(100m, 101m, 99m, 100m, 1_000_000); // Low (99) never reaches Limit (95)
        var order = new Order { Symbol = "TESTSYM", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 100m, LimitPrice = 95m, CorrelationId = "c" };

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.False(result.Filled);
    }

    [Fact]
    public void SellStop_Triggers_WhenBarLowCrossesStop()
    {
        var bar = Bar(100m, 101m, 94m, 95m, 1_000_000);
        var order = new Order { Symbol = "TESTSYM", Side = OrderSide.Sell, Type = OrderType.StopLoss, Quantity = 100m, StopPrice = 96m, CorrelationId = "c" };

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.Filled);
        Assert.True(result.ExecutionPrice <= 96m, "A triggered sell-stop should fill at/below the stop, not better.");
    }

    [Fact]
    public void SellStop_GapThrough_FillsNoBetterThanTheOpen_NotTheStaleStopPrice()
    {
        // Stop is 96, but the bar GAPS DOWN and opens at 90 — a real stop-loss
        // becomes a market order and cannot fill at the stale 96 level.
        var bar = Bar(open: 90m, high: 91m, low: 88m, close: 89m, volume: 1_000_000);
        var order = new Order { Symbol = "TESTSYM", Side = OrderSide.Sell, Type = OrderType.StopLoss, Quantity = 100m, StopPrice = 96m, CorrelationId = "c" };

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.Filled);
        Assert.True(result.ExecutionPrice < 91m, "Gap-through must reference the (worse) Open, not the stale stop price of 96.");
    }

    [Fact]
    public void SellStop_DoesNotTrigger_WhenBarNeverReachesStop()
    {
        var bar = Bar(100m, 101m, 98m, 100m, 1_000_000); // Low (98) never reaches Stop (90)
        var order = new Order { Symbol = "TESTSYM", Side = OrderSide.Sell, Type = OrderType.StopLoss, Quantity = 100m, StopPrice = 90m, CorrelationId = "c" };

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.False(result.Filled);
    }

    [Fact]
    public void LiquidityCap_ProducesPartialFill_WhenOrderExceedsParticipationCap()
    {
        var bar = Bar(100m, 102m, 99m, 101m, 10_000); // 2% of 10,000 = 200 max fillable
        var order = MarketOrder(OrderSide.Buy, 1_000m); // wants far more than the day can support

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.Filled);
        Assert.Equal(200m, result.FilledQuantity);
        Assert.True(result.Reason.Contains("Partial fill"));
    }

    [Fact]
    public void LiquidityCap_ZeroVolumeBar_ProducesNoFill_NeverAFabricatedOne()
    {
        var bar = Bar(100m, 100m, 100m, 100m, 0);
        var order = MarketOrder(OrderSide.Buy, 100m);

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.False(result.Filled);
    }

    [Fact]
    public void TransactionCosts_ArePopulated_OnAFill()
    {
        var bar = Bar(100m, 102m, 99m, 101m, 1_000_000);
        var order = MarketOrder(OrderSide.Buy, 100m);

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.NotNull(result.TransactionCosts);
        Assert.True(result.TransactionCosts!.Total > 0m);
    }

    [Fact]
    public void NoFill_NeverPopulatesTransactionCosts()
    {
        var bar = Bar(100m, 100m, 100m, 100m, 0);
        var order = MarketOrder(OrderSide.Buy, 100m);

        var result = BacktestFillSimulator.Simulate(order, bar, 10_000_000m, MarketCapBucket.Mid, BacktestOptions, RiskOptions);

        Assert.True(result.TransactionCosts is null);
    }
}
