using TradingPlatform.Backtesting.Costs;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class TransactionCostCalculatorTests
{
    private static readonly CommissionOptions Options = new()
    {
        BrokeragePercent = 0.03m,
        BrokerageMaxPerOrder = 20m,
        SttPercent = 0.1m,
        ExchangeChargesPercent = 0.00345m,
        GstPercent = 18m,
        SebiChargesPercent = 0.0001m,
        StampDutyPercent = 0.015m,
    };

    [Fact]
    public void Brokerage_IsPercentOfTurnover_WhenBelowCap()
    {
        var result = TransactionCostCalculator.Calculate(10_000m, OrderSide.Buy, Options);
        Assert.Equal(3m, result.Brokerage); // 10,000 * 0.03% = 3
    }

    [Fact]
    public void Brokerage_IsCapped_WhenPercentWouldExceedMax()
    {
        var result = TransactionCostCalculator.Calculate(1_000_000m, OrderSide.Buy, Options); // 0.03% would be 300
        Assert.Equal(20m, result.Brokerage); // capped at BrokerageMaxPerOrder
    }

    [Fact]
    public void Stt_AppliesOnBothBuyAndSell_DeliveryEquityConvention()
    {
        var buy = TransactionCostCalculator.Calculate(10_000m, OrderSide.Buy, Options);
        var sell = TransactionCostCalculator.Calculate(10_000m, OrderSide.Sell, Options);

        Assert.Equal(10m, buy.Stt);  // 10,000 * 0.1%
        Assert.Equal(10m, sell.Stt); // same on the sell side too
    }

    [Fact]
    public void StampDuty_OnlyAppliesOnBuySide()
    {
        var buy = TransactionCostCalculator.Calculate(10_000m, OrderSide.Buy, Options);
        var sell = TransactionCostCalculator.Calculate(10_000m, OrderSide.Sell, Options);

        Assert.Equal(1.5m, buy.StampDuty);  // 10,000 * 0.015%
        Assert.Equal(0m, sell.StampDuty);
    }

    [Fact]
    public void Gst_AppliesOnlyToBrokeragePlusExchangeCharges_NotToStatutoryLevies()
    {
        var result = TransactionCostCalculator.Calculate(10_000m, OrderSide.Buy, Options);

        var expectedGstBase = result.Brokerage + result.ExchangeCharges;
        var expectedGst = expectedGstBase * Options.GstPercent / 100m;
        Assert.Equal(expectedGst, result.Gst);
    }

    [Fact]
    public void Total_IsSumOfAllComponents()
    {
        var result = TransactionCostCalculator.Calculate(50_000m, OrderSide.Buy, Options);
        var expectedTotal = result.Brokerage + result.Stt + result.ExchangeCharges + result.Gst + result.SebiCharges + result.StampDuty;
        Assert.Equal(expectedTotal, result.Total);
    }

    [Fact]
    public void ZeroTurnover_ProducesZeroCosts()
    {
        var result = TransactionCostCalculator.Calculate(0m, OrderSide.Buy, Options);
        Assert.Equal(0m, result.Total);
    }

    [Fact]
    public void NegativeTurnover_Throws()
    {
        Assert.Throws<ArgumentException>(() => TransactionCostCalculator.Calculate(-1m, OrderSide.Buy, Options));
    }
}
