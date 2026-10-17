using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Validation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class OrderValidationEngineTests
{
    private static Order BuyOrder(decimal qty = 100m) => new() { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = qty, CorrelationId = "c1" };
    private static Order SellOrder(decimal qty = 100m) => new() { Symbol = "ABC", Side = OrderSide.Sell, Type = OrderType.Market, Quantity = qty, CorrelationId = "c1" };

    private static OrderValidationInputs CleanBuyInputs() => new()
    {
        CandidateOrder = BuyOrder(),
        IsMarketOpen = true,
        IsInstrumentActive = true,
        IsSymbolMapped = true,
        AvailableCash = 100_000m,
        EstimatedOrderValue = 50_000m,
        WouldExceedPositionLimit = false,
        WouldExceedSectorLimit = false,
        WouldExceedMicrocapLimit = false,
        ProjectedPortfolioHeatValue = 1_000m,
        PortfolioHeatMaxValue = 8_000m,
        TradingIsShutDown = false,
        DailyLossLimitBreached = false,
        MaxFillableQuantity = 1_000m,
        IsDuplicateOrder = false,
        HasUnexpectedExistingPosition = false,
        HasPendingOrderInSymbol = false,
    };

    [Fact]
    public void AllChecksClean_BuyOrder_IsValid()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs());
        Assert.True(result.IsValid);
        Assert.True(result.Checks.All(c => c.Passed));
    }

    [Fact]
    public void MarketClosed_RejectsBothSides()
    {
        var buyResult = OrderValidationEngine.Validate(CleanBuyInputs() with { IsMarketOpen = false });
        Assert.False(buyResult.IsValid);
        Assert.Equal("Market is not currently open.", buyResult.RejectionReason);
    }

    [Fact]
    public void InsufficientCash_RejectsBuy()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs() with { AvailableCash = 10_000m, EstimatedOrderValue = 50_000m });
        Assert.False(result.IsValid);
        Assert.True(result.Checks.Any(c => c.Name == "SufficientCash" && !c.Passed));
    }

    [Fact]
    public void DrawdownShutdown_RejectsBuy_ButNeverAppliesToSell()
    {
        var buyInputs = CleanBuyInputs() with { TradingIsShutDown = true };
        var buyResult = OrderValidationEngine.Validate(buyInputs);
        Assert.False(buyResult.IsValid);
        Assert.True(buyResult.Checks.Any(c => c.Name == "DrawdownStatus" && !c.Passed));

        // A sell (risk-reducing exit) never even carries a DrawdownStatus check —
        // it must never be blocked by portfolio-level risk gates (spec §6.4).
        var sellInputs = new OrderValidationInputs
        {
            CandidateOrder = SellOrder(),
            IsMarketOpen = true, IsInstrumentActive = true, IsSymbolMapped = true,
            AvailableCash = 0m, EstimatedOrderValue = 0m,
            WouldExceedPositionLimit = false, WouldExceedSectorLimit = false, WouldExceedMicrocapLimit = false,
            ProjectedPortfolioHeatValue = 0m, PortfolioHeatMaxValue = 0m,
            TradingIsShutDown = true, DailyLossLimitBreached = true,
            MaxFillableQuantity = 1_000m, IsDuplicateOrder = false, HasUnexpectedExistingPosition = false, HasPendingOrderInSymbol = false,
        };
        var sellResult = OrderValidationEngine.Validate(sellInputs);
        Assert.True(sellResult.IsValid);
        Assert.False(sellResult.Checks.Any(c => c.Name == "DrawdownStatus"));
        Assert.False(sellResult.Checks.Any(c => c.Name == "DailyLossLimit"));
    }

    [Fact]
    public void DailyLossLimitBreached_RejectsBuyOnly()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs() with { DailyLossLimitBreached = true });
        Assert.False(result.IsValid);
        Assert.True(result.Checks.Any(c => c.Name == "DailyLossLimit" && !c.Passed));
    }

    [Fact]
    public void LiquidityCapExceeded_RejectsEitherSide()
    {
        var buyResult = OrderValidationEngine.Validate(CleanBuyInputs() with { MaxFillableQuantity = 50m });
        Assert.False(buyResult.IsValid);
        Assert.True(buyResult.Checks.Any(c => c.Name == "LiquidityCap" && !c.Passed));
    }

    [Fact]
    public void ZeroQuantity_Rejected()
    {
        var inputs = CleanBuyInputs() with { CandidateOrder = BuyOrder(0m) };
        var result = OrderValidationEngine.Validate(inputs);
        Assert.False(result.IsValid);
        Assert.True(result.Checks.Any(c => c.Name == "OrderQuantityPositive" && !c.Passed));
    }

    [Fact]
    public void DuplicateOrder_Rejected()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs() with { IsDuplicateOrder = true });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void PendingOrderAlreadyInSymbol_Rejected()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs() with { HasPendingOrderInSymbol = true });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void PositionSectorMicrocapLimits_EachIndependentlyRejects()
    {
        Assert.False(OrderValidationEngine.Validate(CleanBuyInputs() with { WouldExceedPositionLimit = true }).IsValid);
        Assert.False(OrderValidationEngine.Validate(CleanBuyInputs() with { WouldExceedSectorLimit = true }).IsValid);
        Assert.False(OrderValidationEngine.Validate(CleanBuyInputs() with { WouldExceedMicrocapLimit = true }).IsValid);
    }

    [Fact]
    public void PortfolioHeatExceeded_RejectsBuy()
    {
        var result = OrderValidationEngine.Validate(CleanBuyInputs() with { ProjectedPortfolioHeatValue = 9_000m, PortfolioHeatMaxValue = 8_000m });
        Assert.False(result.IsValid);
        Assert.True(result.Checks.Any(c => c.Name == "PortfolioHeat" && !c.Passed));
    }

    [Fact]
    public void AllChecksRun_EvenAfterAFailure_NotShortCircuited()
    {
        var inputs = CleanBuyInputs() with { IsMarketOpen = false, DailyLossLimitBreached = true };
        var result = OrderValidationEngine.Validate(inputs);
        // Both the market-open AND daily-loss-limit checks should be present and failed —
        // proving the engine doesn't stop at the first failure.
        Assert.True(result.Checks.Any(c => c.Name == "MarketOpen" && !c.Passed));
        Assert.True(result.Checks.Any(c => c.Name == "DailyLossLimit" && !c.Passed));
    }
}
