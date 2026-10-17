using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Execution.OrderManagement;
using TradingPlatform.Execution.Orchestration;
using TradingPlatform.Execution.Paper;
using TradingPlatform.Execution.Validation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class LiveTradingCycleEngineTests
{
    private static readonly RiskOptions RiskOptions = new() { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() };
    private static readonly LiveTradingOptions LiveTradingOptions = new()
    {
        RejectionConsecutiveThreshold = 3, AbnormalSlippageThresholdBps = 100m, AbnormalSlippageConsecutiveThreshold = 3, CashReconciliationToleranceRupees = 1m,
    };

    private sealed class FakeQuoteSource : IQuoteSource
    {
        public Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaperQuote(
                new HistoricalBar { Symbol = symbol, Timestamp = DateTimeOffset.UtcNow, Open = 100m, High = 102m, Low = 99m, Close = 101m, AdjustedClose = 101m, Volume = 1_000_000, Source = "Fixture", Timeframe = Timeframe.Daily },
                10_000_000m, MarketCapBucket.Mid));
    }

    private static LiveTradingCycleEngine BuildEngine() => new(new OrderManager(new PaperExecutionProvider(new FakeQuoteSource(), new BacktestOptions(), RiskOptions)));

    private static Position Pos(string symbol, decimal qty) => new() { Symbol = symbol, Quantity = qty, IsOpen = true };

    private static Order BuyOrder(string symbol = "ABC") => new() { Symbol = symbol, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 10m, CorrelationId = "c1" };

    private static OrderValidationInputs CleanValidation(Order order) => new()
    {
        CandidateOrder = order, IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
        AvailableCash = 1_000_000m, EstimatedOrderValue = 1_000m,
        WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
        ProjectedPortfolioHeatValue = 1_000m, PortfolioHeatMaxValue = 8_000m,
        TradingIsShutDown = false, DailyLossLimitBreached = false,
        MaxFillableQuantity = 1_000m, IsDuplicateOrder = false, HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
    };

    private static LiveTradingCycleInputs BaseInputs() => new()
    {
        AsOfDate = new DateOnly(2026, 1, 5),
        Now = DateTimeOffset.UtcNow,
        Nav = 1_000_000m,
        PreviousDrawdownState = null,
        PreviousLossLimitState = null,
        CurrentKillSwitchState = KillSwitchState.Inactive,
        RiskOptions = RiskOptions,
        LiveTradingOptions = LiveTradingOptions,
        LocalOpenPositions = Array.Empty<Position>(),
        BrokerOpenPositions = Array.Empty<Position>(),
        LocalCashBalance = 1_000_000m,
        BrokerCashBalance = 1_000_000m,
        RecentOrderRejections = Array.Empty<bool>(),
        RecentSlippageBps = Array.Empty<decimal>(),
        ExitIntents = Array.Empty<(Order, OrderValidationInputs)>(),
        EntryIntents = Array.Empty<(Order, OrderValidationInputs)>(),
    };

    [Fact]
    public async Task CleanCycle_SubmitsEntryOrder_Successfully()
    {
        var engine = BuildEngine();
        var order = BuyOrder();
        var inputs = BaseInputs() with { EntryIntents = new[] { (order, CleanValidation(order)) } };

        var result = await engine.RunCycleAsync(inputs);

        Assert.Equal(1, result.SubmittedOrders.Count);
        Assert.True(result.SubmittedOrders[0].Accepted);
        Assert.False(result.KillSwitchState.IsActive);
    }

    [Fact]
    public async Task ReconciliationMismatch_ActivatesKillSwitch_AndBlocksTheEntry()
    {
        var engine = BuildEngine();
        var order = BuyOrder();
        var inputs = BaseInputs() with
        {
            LocalOpenPositions = new[] { Pos("XYZ", 10m) },
            BrokerOpenPositions = new[] { Pos("XYZ", 5m) }, // mismatch
            EntryIntents = new[] { (order, CleanValidation(order)) },
        };

        var result = await engine.RunCycleAsync(inputs);

        Assert.True(result.KillSwitchState.IsActive);
        Assert.Equal(KillSwitchSource.ReconciliationError, result.KillSwitchState.Source);
        Assert.False(result.SubmittedOrders[0].Accepted);
    }

    [Fact]
    public async Task DrawdownShutdown_ActivatesKillSwitch()
    {
        var engine = BuildEngine();

        // First cycle establishes the peak; second cycle shows the crash.
        var firstResult = await engine.RunCycleAsync(BaseInputs() with { Nav = 1_000_000m });
        var secondInputs = BaseInputs() with { Nav = 580_000m, PreviousDrawdownState = firstResult.DrawdownState, CurrentKillSwitchState = firstResult.KillSwitchState };
        var secondResult = await engine.RunCycleAsync(secondInputs);

        Assert.True(secondResult.KillSwitchState.IsActive);
        Assert.Equal(KillSwitchSource.Drawdown, secondResult.KillSwitchState.Source);
    }

    [Fact]
    public async Task KillSwitchActive_ExitOrdersStillGoThrough_RiskReducingCarveOut()
    {
        var engine = BuildEngine();
        var activeKillSwitch = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Drawdown, "test halt", DateTimeOffset.UtcNow);

        var sellOrder = new Order { Symbol = "ABC", Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 10m, CorrelationId = "c1" };
        var sellValidation = new OrderValidationInputs
        {
            CandidateOrder = sellOrder, IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
            AvailableCash = 0m, EstimatedOrderValue = 0m, WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
            ProjectedPortfolioHeatValue = 0m, PortfolioHeatMaxValue = 0m, TradingIsShutDown = true, DailyLossLimitBreached = true,
            MaxFillableQuantity = 1_000m, IsDuplicateOrder = false, HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
        };

        var inputs = BaseInputs() with { CurrentKillSwitchState = activeKillSwitch, ExitIntents = new[] { (sellOrder, sellValidation) } };

        var result = await engine.RunCycleAsync(inputs);

        Assert.True(result.SubmittedOrders[0].Accepted);
    }

    [Fact]
    public async Task RepeatedRejections_ActivatesKillSwitch()
    {
        var engine = BuildEngine();
        var order = BuyOrder();
        var inputs = BaseInputs() with
        {
            RecentOrderRejections = new[] { true, true, true },
            EntryIntents = new[] { (order, CleanValidation(order)) },
        };

        var result = await engine.RunCycleAsync(inputs);

        Assert.True(result.KillSwitchState.IsActive);
        Assert.Equal(KillSwitchSource.RepeatedOrderRejection, result.KillSwitchState.Source);
    }

    [Fact]
    public async Task NoTriggers_KillSwitchStaysInactive()
    {
        var engine = BuildEngine();
        var result = await engine.RunCycleAsync(BaseInputs());

        Assert.False(result.KillSwitchState.IsActive);
    }
}
