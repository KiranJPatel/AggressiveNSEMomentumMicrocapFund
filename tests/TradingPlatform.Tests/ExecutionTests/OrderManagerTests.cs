using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Execution.OrderManagement;
using TradingPlatform.Execution.Paper;
using TradingPlatform.Execution.Validation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class OrderManagerTests
{
    private sealed class FakeQuoteSource : IQuoteSource
    {
        public Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaperQuote(
                new HistoricalBar { Symbol = symbol, Timestamp = DateTimeOffset.UtcNow, Open = 100m, High = 102m, Low = 99m, Close = 101m, AdjustedClose = 101m, Volume = 1_000_000, Source = "Fixture", Timeframe = Timeframe.Daily },
                10_000_000m, MarketCapBucket.Mid));
    }

    private static OrderManager BuildManager() => new(new PaperExecutionProvider(new FakeQuoteSource(), new BacktestOptions(), new RiskOptions()));

    private static Order BuyOrder() => new() { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 100m, CorrelationId = "c1" };

    private static OrderValidationInputs CleanBuyInputs(Order order) => new()
    {
        CandidateOrder = order,
        IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
        AvailableCash = 1_000_000m, EstimatedOrderValue = 10_000m,
        WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
        ProjectedPortfolioHeatValue = 1_000m, PortfolioHeatMaxValue = 8_000m,
        TradingIsShutDown = false, DailyLossLimitBreached = false,
        MaxFillableQuantity = 1_000m, IsDuplicateOrder = false, HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
    };

    [Fact]
    public async Task SubmitAsync_CleanOrder_IsAccepted_AndFillsThroughToProvider()
    {
        var manager = BuildManager();
        var order = BuyOrder();

        var outcome = await manager.SubmitAsync(order, CleanBuyInputs(order), KillSwitchState.Inactive);

        Assert.True(outcome.Accepted);
        Assert.Equal(OrderState.Filled, outcome.Order.State);
        Assert.True(outcome.Validation.IsValid);
    }

    [Fact]
    public async Task SubmitAsync_FailedValidation_NeverReachesExecutionProvider()
    {
        var manager = BuildManager();
        var order = BuyOrder();
        var inputs = CleanBuyInputs(order) with { IsMarketOpen = false };

        var outcome = await manager.SubmitAsync(order, inputs, KillSwitchState.Inactive);

        Assert.False(outcome.Accepted);
        Assert.Equal(OrderState.Rejected, outcome.Order.State);
        Assert.False(outcome.Validation.IsValid);
    }

    [Fact]
    public async Task SubmitAsync_EventTrail_RecordsEveryTransition()
    {
        var manager = BuildManager();
        var order = BuyOrder();

        var outcome = await manager.SubmitAsync(order, CleanBuyInputs(order), KillSwitchState.Inactive);

        // Created -> Validated, then Validated -> Filled (via the provider)
        Assert.Equal(2, outcome.Events.Count);
        Assert.Equal(OrderState.Created, outcome.Events[0].FromState);
        Assert.Equal(OrderState.Validated, outcome.Events[0].ToState);
        Assert.Equal(OrderState.Validated, outcome.Events[1].FromState);
        Assert.Equal(OrderState.Filled, outcome.Events[1].ToState);
    }

    [Fact]
    public async Task SubmitAsync_KillSwitchActive_BlocksBuy_NeverReachesProvider()
    {
        var manager = BuildManager();
        var order = BuyOrder();
        var killSwitch = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Drawdown, "40% drawdown", DateTimeOffset.UtcNow);

        var outcome = await manager.SubmitAsync(order, CleanBuyInputs(order), killSwitch);

        Assert.False(outcome.Accepted);
        Assert.Equal(OrderState.Rejected, outcome.Order.State);
        // Only the Created->Validated->Rejected transition, never a provider-side transition.
        Assert.Equal(2, outcome.Events.Count);
    }

    [Fact]
    public async Task SubmitAsync_KillSwitchActive_StillAllowsSell()
    {
        var manager = BuildManager();
        var order = new Order { Symbol = "ABC", Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 100m, CorrelationId = "c1" };
        var sellInputs = new OrderValidationInputs
        {
            CandidateOrder = order, IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
            AvailableCash = 0m, EstimatedOrderValue = 0m, WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
            ProjectedPortfolioHeatValue = 0m, PortfolioHeatMaxValue = 0m, TradingIsShutDown = true, DailyLossLimitBreached = true,
            MaxFillableQuantity = 1_000m, IsDuplicateOrder = false, HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
        };
        var killSwitch = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Drawdown, "40% drawdown", DateTimeOffset.UtcNow);

        var outcome = await manager.SubmitAsync(order, sellInputs, killSwitch);

        Assert.True(outcome.Accepted);
    }
}
