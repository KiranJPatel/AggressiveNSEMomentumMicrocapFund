using TradingPlatform.Backtesting.Slippage;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;
using static TradingPlatform.Tests.BacktestingTests.BacktestBarFixture;

namespace TradingPlatform.Tests.BacktestingTests;

public class SlippageCalculatorTests
{
    private static readonly BacktestOptions FixedOptions = new() { SlippageModel = SlippageModelType.FixedBps, FixedSlippageBps = 10m };
    private static readonly BacktestOptions PercentageOptions = new() { SlippageModel = SlippageModelType.Percentage, PercentageSlippageOfRangePercent = 10m };
    private static readonly BacktestOptions LiquidityOptions = new()
    {
        SlippageModel = SlippageModelType.LiquidityBased,
        LiquidityBasedMinBps = 5m,
        LiquidityBasedMaxParticipationBpsScaling = 50m,
        LiquidityBasedMicrocapMultiplier = 2m,
    };

    [Fact]
    public void FixedBps_AddsForBuy_SubtractsForSell()
    {
        var bar = Bar(100m, 102m, 98m, 101m, 100_000);

        var buy = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, bar, 10_000_000m, MarketCapBucket.Mid, FixedOptions);
        var sell = SlippageCalculator.Calculate(100m, OrderSide.Sell, 100m, bar, 10_000_000m, MarketCapBucket.Mid, FixedOptions);

        Assert.Equal(100.10m, buy.ExecutionPrice);  // 100 * 10bps = 0.10
        Assert.Equal(99.90m, sell.ExecutionPrice);
        Assert.Equal(10m, buy.SlippageBps);
    }

    [Fact]
    public void Percentage_ScalesWithBarRange_NotJustPrice()
    {
        var narrowBar = Bar(100m, 100.50m, 99.50m, 100m, 100_000); // 1.00 range
        var wideBar = Bar(100m, 105m, 95m, 100m, 100_000);          // 10.00 range

        var narrow = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, narrowBar, 10_000_000m, MarketCapBucket.Mid, PercentageOptions);
        var wide = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, wideBar, 10_000_000m, MarketCapBucket.Mid, PercentageOptions);

        Assert.True(wide.SlippageBps > narrow.SlippageBps, "A wider-range day must produce more slippage under the Percentage (volatility-scaled) model.");
    }

    [Fact]
    public void LiquidityBased_WidensAsOrderSizeApproachesFullParticipation()
    {
        var bar = Bar(100m, 102m, 98m, 101m, 500_000);

        var small = SlippageCalculator.Calculate(100m, OrderSide.Buy, 10m, bar, 1_000_000m, MarketCapBucket.Mid, LiquidityOptions);   // 0.1% of ADTV
        var large = SlippageCalculator.Calculate(100m, OrderSide.Buy, 10_000m, bar, 1_000_000m, MarketCapBucket.Mid, LiquidityOptions); // >100% of ADTV, clamped

        Assert.True(large.SlippageBps > small.SlippageBps);
        Assert.Equal(LiquidityOptions.LiquidityBasedMinBps + LiquidityOptions.LiquidityBasedMaxParticipationBpsScaling, large.SlippageBps); // clamped at full participation
    }

    [Fact]
    public void LiquidityBased_WidensFurther_ForMicrocaps()
    {
        var bar = Bar(100m, 102m, 98m, 101m, 500_000);

        var mid = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, bar, 1_000_000m, MarketCapBucket.Mid, LiquidityOptions);
        var micro = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, bar, 1_000_000m, MarketCapBucket.Micro, LiquidityOptions);

        Assert.Equal(mid.SlippageBps * LiquidityOptions.LiquidityBasedMicrocapMultiplier, micro.SlippageBps);
    }

    [Fact]
    public void LiquidityBased_NoAdtvData_TreatedAsMaximallyIlliquid_NotFrictionless()
    {
        var bar = Bar(100m, 102m, 98m, 101m, 500_000);

        var noAdtv = SlippageCalculator.Calculate(100m, OrderSide.Buy, 100m, bar, 0m, MarketCapBucket.Mid, LiquidityOptions);
        var fullParticipation = SlippageCalculator.Calculate(100m, OrderSide.Buy, 10_000m, bar, 1_000_000m, MarketCapBucket.Mid, LiquidityOptions);

        Assert.Equal(fullParticipation.SlippageBps, noAdtv.SlippageBps); // treated the same as 100% participation, not 0%
    }

    [Fact]
    public void SlippageNeverImprovesTheFill_RegardlessOfModel()
    {
        var bar = Bar(100m, 102m, 98m, 101m, 500_000);
        foreach (var options in new[] { FixedOptions, PercentageOptions, LiquidityOptions })
        {
            var buy = SlippageCalculator.Calculate(100m, OrderSide.Buy, 50m, bar, 1_000_000m, MarketCapBucket.Mid, options);
            var sell = SlippageCalculator.Calculate(100m, OrderSide.Sell, 50m, bar, 1_000_000m, MarketCapBucket.Mid, options);

            Assert.True(buy.ExecutionPrice >= 100m, $"{options.SlippageModel}: a buy must never fill better (lower) than the reference price.");
            Assert.True(sell.ExecutionPrice <= 100m, $"{options.SlippageModel}: a sell must never fill better (higher) than the reference price.");
        }
    }
}
