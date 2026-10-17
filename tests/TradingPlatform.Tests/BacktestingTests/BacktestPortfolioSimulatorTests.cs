using TradingPlatform.Backtesting.Costs;
using TradingPlatform.Backtesting.Portfolio;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class BacktestPortfolioSimulatorTests
{
    private static readonly TransactionCostBreakdown NoCosts = new(0m, 0m, 0m, 0m, 0m, 0m, 0m);
    private static readonly DateTimeOffset Day0 = new(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(5.5));

    private static TransactionCostBreakdown Costs(decimal total) => new(total, 0m, 0m, 0m, 0m, 0m, total);

    [Fact]
    public void ApplyBuyFill_NewPosition_DeductsCash_AndOpensLot()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);

        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, Costs(50m), Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 5000m, newPositionTotalTargetQuantity: 100m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        Assert.Equal(1_000_000m - 50_000m - 50m, state.Cash); // 100*500 + 50 fee
        Assert.True(state.OpenPositions.ContainsKey("ABC"));
        var lot = state.OpenPositions["ABC"];
        Assert.Equal(100m, lot.Quantity);
        Assert.Equal(500m, lot.AverageEntryPrice);
        Assert.Equal(480m, lot.CurrentStop);
    }

    [Fact]
    public void ApplyBuyFill_AddToExistingPosition_ComputesWeightedAverageEntryPrice()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 40m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 5000m, newPositionTotalTargetQuantity: 40m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage2Add, 30m, 520m, NoCosts, Day0.AddDays(3));

        var lot = state.OpenPositions["ABC"];
        Assert.Equal(70m, lot.Quantity);
        // (40*500 + 30*520) / 70 = 508.571...
        var expected = (40m * 500m + 30m * 520m) / 70m;
        Assert.Equal(expected, lot.AverageEntryPrice);
        Assert.Equal(20m, lot.RiskPerShare); // fixed at Stage 1, unchanged by the Stage 2 add
        Assert.Equal(480m, lot.InitialStop);  // likewise fixed
    }

    [Fact]
    public void ApplyBuyFill_ToExistingPosition_WithoutNewPositionParams_Throws_ForNewSymbol()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        Assert.Throws<InvalidOperationException>(() =>
            BacktestPortfolioSimulator.ApplyBuyFill(state, "XYZ", PyramidStage.Stage1Initial, 10m, 100m, NoCosts, Day0));
    }

    [Fact]
    public void ApplySellFill_FullQuantity_RealizesProfit_NetOfAllFees_AndClosesPosition()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, Costs(50m), Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 30m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var outcome = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 100m, 550m, Costs(60m), Day0.AddDays(10), ExitReason.TrailingStopHit);

        // Bought: 100*500 + 50 = 50,050. Sold: 100*550 - 60 = 54,940. PnL = 4,890.
        Assert.True(outcome.FullyClosed);
        Assert.Equal(4_890m, outcome.RealizedPnl);
        Assert.False(state.OpenPositions.ContainsKey("ABC"));
        Assert.True(state.ClosedTrades.Contains(outcome.Trade!));
    }

    [Fact]
    public void ApplySellFill_FullQuantity_ComputesRMultiple_AgainstFixedInitialRiskAmount()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 100m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var outcome = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 100m, 540m, NoCosts, Day0.AddDays(5), ExitReason.TrailingStopHit);

        // PnL = (540-500)*100 = 4,000. RMultiple = 4,000 / 2,000 = 2.0
        Assert.Equal(2.0m, outcome.Trade!.RMultiple);
    }

    [Fact]
    public void UpdateExcursion_TracksHighestAndLowestPriceSinceEntry()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 100m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");
        var lot = state.OpenPositions["ABC"];

        BacktestPortfolioSimulator.UpdateExcursion(lot, barHigh: 530m, barLow: 495m);
        BacktestPortfolioSimulator.UpdateExcursion(lot, barHigh: 510m, barLow: 470m); // lower low, lower high

        Assert.Equal(530m, lot.HighestPriceSinceEntry); // still the earlier, higher high
        Assert.Equal(470m, lot.LowestPriceSinceEntry);   // the new, lower low

        var outcome = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 100m, 505m, NoCosts, Day0.AddDays(2), ExitReason.StopLoss);
        // MFE = (530-500)/20 = 1.5R ; MAE = (470-500)/20 = -1.5R
        Assert.Equal(1.5m, outcome.Trade!.MaximumFavorableExcursion);
        Assert.Equal(-1.5m, outcome.Trade!.MaximumAdverseExcursion);
    }

    [Fact]
    public void ApplySellFill_FullQuantity_ProducesOneTradeLegPerEntryFill()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 40m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 40m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage2Add, 30m, 520m, NoCosts, Day0.AddDays(3));
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage3Add, 30m, 540m, NoCosts, Day0.AddDays(6));

        var outcome = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 100m, 600m, NoCosts, Day0.AddDays(20), ExitReason.TrailingStopHit);

        var legsForTrade = state.ClosedTradeLegs.Where(l => l.TradeId == outcome.Trade!.Id).ToList();
        Assert.Equal(3, legsForTrade.Count);
    }

    [Fact]
    public void ApplySellFill_UnknownSymbol_Throws()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        Assert.Throws<InvalidOperationException>(() =>
            BacktestPortfolioSimulator.ApplySellFill(state, "NOPE", 100m, 100m, NoCosts, Day0, ExitReason.ManualClose));
    }

    [Fact]
    public void ApplySellFill_PartialQuantity_KeepsPositionOpen_NoTradeYet()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 30m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var outcome = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 40m, 550m, NoCosts, Day0.AddDays(5), ExitReason.TrailingStopHit);

        Assert.False(outcome.FullyClosed);
        Assert.True(outcome.Trade is null);
        Assert.True(state.OpenPositions.ContainsKey("ABC"));
        Assert.Equal(60m, state.OpenPositions["ABC"].Quantity); // 100 - 40
        Assert.Equal(0, state.ClosedTrades.Count); // no Trade record until fully flat
    }

    [Fact]
    public void ApplySellFill_MultiDayPartialExits_EventuallyProduceOneTrade_WithWeightedAverageExitPrice()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 100m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var day1 = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 60m, 540m, NoCosts, Day0.AddDays(1), ExitReason.MomentumFailure);
        Assert.False(day1.FullyClosed);

        var day2 = BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 40m, 560m, NoCosts, Day0.AddDays(2), ExitReason.MomentumFailure);
        Assert.True(day2.FullyClosed);

        // Weighted avg exit = (60*540 + 40*560) / 100 = 548
        Assert.Equal(548m, day2.Trade!.ExitPrice);
        // Total PnL = (60*540 - 60*500) + (40*560 - 40*500) = 2,400 + 2,400 = 4,800
        Assert.Equal(4_800m, day2.Trade!.RealizedPnl);
        Assert.Equal(1, state.ClosedTrades.Count); // exactly one Trade for the whole lifecycle, not two
        Assert.False(state.OpenPositions.ContainsKey("ABC"));
    }

    [Fact]
    public void ApplySellFill_QuantityExceedingOpenPosition_Throws()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 50m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 1000m, newPositionTotalTargetQuantity: 50m,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        Assert.Throws<ArgumentException>(() =>
            BacktestPortfolioSimulator.ApplySellFill(state, "ABC", 60m, 500m, NoCosts, Day0.AddDays(1), ExitReason.ManualClose));
    }
}
