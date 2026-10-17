using TradingPlatform.Backtesting.Portfolio;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class PortfolioExposureCalculatorTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly TradingPlatform.Backtesting.Costs.TransactionCostBreakdown NoCosts = new(0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static BacktestPortfolioState BuildStateWithOnePosition(decimal qty, decimal entryPrice, decimal stop)
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, qty, entryPrice, NoCosts, Day0,
            newPositionInitialStop: stop, newPositionRiskPerShare: entryPrice - stop, newPositionInitialRiskAmount: qty * (entryPrice - stop),
            newPositionTotalTargetQuantity: qty,
            newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");
        return state;
    }

    [Fact]
    public void ComputeNav_IsCashPlusMarkedPositionValue()
    {
        var state = BuildStateWithOnePosition(100m, 500m, 480m); // cash = 1,000,000 - 50,000 = 950,000
        var prices = new Dictionary<string, decimal> { ["ABC"] = 520m };

        var nav = PortfolioExposureCalculator.ComputeNav(state, prices);

        Assert.Equal(950_000m + 100m * 520m, nav);
    }

    [Fact]
    public void ComputeNav_FallsBackToAverageEntryPrice_WhenTodaysMarkIsMissing()
    {
        var state = BuildStateWithOnePosition(100m, 500m, 480m);
        var prices = new Dictionary<string, decimal>(); // no mark for ABC today (e.g. trading halt)

        var nav = PortfolioExposureCalculator.ComputeNav(state, prices);

        Assert.Equal(950_000m + 100m * 500m, nav); // falls back to entry price, never drops the position
    }

    [Fact]
    public void ComputePortfolioHeatValue_UsesLiveCurrentStop_NotFrozenInitialRisk()
    {
        var state = BuildStateWithOnePosition(100m, 500m, 480m); // InitialRiskAmount = 100*20 = 2,000
        state.OpenPositions["ABC"].CurrentStop = 495m; // trailed up since entry
        var prices = new Dictionary<string, decimal> { ["ABC"] = 510m };

        var heat = PortfolioExposureCalculator.ComputePortfolioHeatValue(state, prices);

        // Live risk = 100 * (510 - 495) = 1,500 -- less than the frozen InitialRiskAmount of 2,000, because the stop has trailed up.
        Assert.Equal(1_500m, heat);
    }

    [Fact]
    public void ComputePortfolioHeatValue_NeverNegative_WhenPriceBelowStop()
    {
        var state = BuildStateWithOnePosition(100m, 500m, 480m);
        var prices = new Dictionary<string, decimal> { ["ABC"] = 470m }; // price has fallen below the stop (not yet processed as an exit)

        var heat = PortfolioExposureCalculator.ComputePortfolioHeatValue(state, prices);

        Assert.Equal(0m, heat);
    }

    [Fact]
    public void ComputeSectorExposureValue_SumsOnlyMatchingSector()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 100m, newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");
        BacktestPortfolioSimulator.ApplyBuyFill(state, "XYZ", PyramidStage.Stage1Initial, 50m, 200m, NoCosts, Day0,
            newPositionInitialStop: 190m, newPositionRiskPerShare: 10m, newPositionInitialRiskAmount: 500m, newPositionTotalTargetQuantity: 50m, newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var prices = new Dictionary<string, decimal> { ["ABC"] = 500m, ["XYZ"] = 200m };
        var sectors = new Dictionary<string, string?> { ["ABC"] = "IT", ["XYZ"] = "Pharma" };

        var itExposure = PortfolioExposureCalculator.ComputeSectorExposureValue(state, prices, sectors, "IT");

        Assert.Equal(100m * 500m, itExposure); // only ABC counted
    }

    [Fact]
    public void ComputeSectorExposureValue_UnknownSector_NeverAggregatesAgainstAnyNamedSector()
    {
        var state = BuildStateWithOnePosition(100m, 500m, 480m);
        var prices = new Dictionary<string, decimal> { ["ABC"] = 500m };
        var sectors = new Dictionary<string, string?>(); // ABC's sector is Unknown (absent)

        var exposure = PortfolioExposureCalculator.ComputeSectorExposureValue(state, prices, sectors, "IT");

        Assert.Equal(0m, exposure);
    }

    [Fact]
    public void ComputeMicrocapExposureValue_SumsOnlyMicrocapPositions()
    {
        var state = BacktestPortfolioState.Initial(1_000_000m);
        BacktestPortfolioSimulator.ApplyBuyFill(state, "ABC", PyramidStage.Stage1Initial, 100m, 500m, NoCosts, Day0,
            newPositionInitialStop: 480m, newPositionRiskPerShare: 20m, newPositionInitialRiskAmount: 2000m, newPositionTotalTargetQuantity: 100m, newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");
        BacktestPortfolioSimulator.ApplyBuyFill(state, "XYZ", PyramidStage.Stage1Initial, 50m, 200m, NoCosts, Day0,
            newPositionInitialStop: 190m, newPositionRiskPerShare: 10m, newPositionInitialRiskAmount: 500m, newPositionTotalTargetQuantity: 50m, newPositionStrategyVersion: "v1", newPositionConfigurationVersion: "c1");

        var prices = new Dictionary<string, decimal> { ["ABC"] = 500m, ["XYZ"] = 200m };
        var buckets = new Dictionary<string, MarketCapBucket> { ["ABC"] = MarketCapBucket.Mid, ["XYZ"] = MarketCapBucket.Micro };

        var microExposure = PortfolioExposureCalculator.ComputeMicrocapExposureValue(state, prices, buckets);

        Assert.Equal(50m * 200m, microExposure); // only XYZ counted
    }
}
