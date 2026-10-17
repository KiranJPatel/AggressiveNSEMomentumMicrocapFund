using System.Text.Json;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class StagedEntryTests
{
    [Fact]
    public void PortfolioHeatCap_IsRupeesOfNav_NotTheRawPercent()
    {
        // Regression: the raw percent (8) was compared against rupee heat, so every paper/live entry failed validation.
        Assert.Equal(80_000m, TradeCycleCommand.PortfolioHeatCap(1_000_000m, new RiskOptions { PortfolioHeatMaxPercent = 8m }));
    }

    [Fact]
    public void Split_40_30_30_SumsToTotal()
    {
        var o = new StrategyOptions();
        var (s1, s2, s3) = TradingPlatform.Strategy.Entry.StagedEntryCalculator.Split(100m, o);
        Assert.Equal(40m, s1); Assert.Equal(30m, s2); Assert.Equal(30m, s3);
    }

    [Fact]
    public void Planner_NoPlan_WhenNothingStagedOrNoBars()
    {
        var pos = new Position { Symbol = "ABC", Quantity = 40, AverageEntryPrice = 100m, CurrentStop = 95m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true };
        var staging = new PositionStaging(0m, 100m, DateTimeOffset.UtcNow.AddDays(-5));
        Assert.Null(StagedAddPlanner.Plan(pos, staging, new List<HistoricalBar>(), new IndicatorSnapshot { Symbol = "ABC" }, new StrategyOptions()));
    }

    [Fact]
    public void Planner_NoPlan_WhenAlreadyAtStage3()
    {
        var pos = new Position { Symbol = "ABC", Quantity = 100, AverageEntryPrice = 100m, CurrentStop = 95m, HighestStageFilled = PyramidStage.Stage3Add, IsOpen = true };
        var staging = new PositionStaging(100m, 100m, DateTimeOffset.UtcNow.AddDays(-5));
        var bars = new List<HistoricalBar> { new() { Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow, Open = 110, High = 112, Low = 109, Close = 111, Volume = 1000, Source = "test" } };
        Assert.Null(StagedAddPlanner.Plan(pos, staging, bars, new IndicatorSnapshot { Symbol = "ABC" }, new StrategyOptions()));
    }

    [Fact]
    public void Planner_Stage2_Triggers_WhenMoveTrendAndStopAllPass_AndUsesTheOriginalSplit()
    {
        var pos = new Position { Symbol = "ABC", Quantity = 40, AverageEntryPrice = 100m, CurrentStop = 95m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true };
        var staging = new PositionStaging(100m, 100m, DateTimeOffset.UtcNow.AddDays(-5));
        var bars = new List<HistoricalBar> { new() { Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow, Open = 104, High = 108, Low = 103, Close = 106, Volume = 1000, Source = "test" } };
        var ind = new IndicatorSnapshot { Symbol = "ABC", HmaFast = 105m, HmaSlow = 100m };

        var plan = StagedAddPlanner.Plan(pos, staging, bars, ind, new StrategyOptions());
        Assert.NotNull(plan);
        Assert.Equal(PyramidStage.Stage2Add, plan!.Stage);
        Assert.Equal(30m, plan.Quantity);
    }

    [Fact]
    public void Planner_Stage2_DoesNotTrigger_WhenPriceIsBelowStage1Entry_NeverAddToALoser()
    {
        var pos = new Position { Symbol = "ABC", Quantity = 40, AverageEntryPrice = 100m, CurrentStop = 90m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true };
        var staging = new PositionStaging(100m, 100m, DateTimeOffset.UtcNow.AddDays(-5));
        var bars = new List<HistoricalBar> { new() { Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow, Open = 98, High = 99, Low = 96, Close = 97, Volume = 1000, Source = "test" } };
        var ind = new IndicatorSnapshot { Symbol = "ABC", HmaFast = 105m, HmaSlow = 100m };
        Assert.Null(StagedAddPlanner.Plan(pos, staging, bars, ind, new StrategyOptions()));
    }

    [Fact]
    public void Planner_Stage2_DoesNotTrigger_WhenTrendIndicatorsMissing_FailsClosed()
    {
        var pos = new Position { Symbol = "ABC", Quantity = 40, AverageEntryPrice = 100m, CurrentStop = 95m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true };
        var staging = new PositionStaging(100m, 100m, DateTimeOffset.UtcNow.AddDays(-5));
        var bars = new List<HistoricalBar> { new() { Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow, Open = 104, High = 108, Low = 103, Close = 106, Volume = 1000, Source = "test" } };
        Assert.Null(StagedAddPlanner.Plan(pos, staging, bars, new IndicatorSnapshot { Symbol = "ABC" }, new StrategyOptions()));
    }

    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public IOrderRepository Orders => Host.Repo<IOrderRepository>();
        public IPositionRepository Positions => Host.Repo<IPositionRepository>();
        public ITradeRepository Trades => Host.Repo<ITradeRepository>();
        public ISignalRepository Signals => Host.Repo<ISignalRepository>();
        public IRiskEventRepository Risk => Host.Repo<IRiskEventRepository>();
        public IEngineStateRepository State => Host.Repo<IEngineStateRepository>();
        public void Dispose() => Host.Dispose();

        public async Task<decimal> ApplyAsync(OrderSide side, string sym, decimal qty, decimal price, OrderIntent intent, Position? exitPos = null)
        {
            var o = new Order { Symbol = sym, Side = side, Type = OrderType.Market, State = OrderState.Filled, Quantity = qty, CorrelationId = Guid.NewGuid().ToString(),
                                FilledQuantity = qty, AverageFillPrice = price, FillCommission = 0m };
            var id = await Orders.InsertAsync(o);
            return await FillApplier.ApplyAsync(o, id, intent, exitPos, Orders, Positions, Trades, Signals, Risk, State, default);
        }
    }

    [Fact]
    public async Task Stage1Fill_OpensPartialPosition_AndRemembersTheTotalTarget()
    {
        using var c = new Ctx();
        var intent = new OrderIntent("Entry", "ABC", 100m, 101m, 95m, 100m, null, PyramidStage.Stage1Initial, 100m);
        var cash = await c.ApplyAsync(OrderSide.Buy, "ABC", 40, 102m, intent);

        Assert.Equal(-(40 * 102m), cash);
        var pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(40m, pos.Quantity);
        Assert.Equal(PyramidStage.Stage1Initial, pos.HighestStageFilled);
        var staging = JsonSerializer.Deserialize<PositionStaging>((await c.State.GetAsync(PositionStaging.StateKey("ABC")))!)!;
        Assert.Equal(100m, staging.TotalTargetQuantity);
        Assert.Equal(102m, staging.Stage1EntryPrice);
    }

    [Fact]
    public async Task Stage2And3Adds_GrowPositionWithWeightedAverage_AndAdvanceStage()
    {
        using var c = new Ctx();
        await c.ApplyAsync(OrderSide.Buy, "ABC", 40, 100m, new OrderIntent("Entry", "ABC", 99m, 100m, 95m, 100m, null, PyramidStage.Stage1Initial, 100m));

        await c.ApplyAsync(OrderSide.Buy, "ABC", 30, 110m, new OrderIntent("Add", "ABC", 0m, 100m, 95m, 30m, null, PyramidStage.Stage2Add));
        var pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(70m, pos.Quantity);
        Assert.Equal(PyramidStage.Stage2Add, pos.HighestStageFilled);
        Assert.Equal((40 * 100m + 30 * 110m) / 70m, pos.AverageEntryPrice);
        var staging = JsonSerializer.Deserialize<PositionStaging>((await c.State.GetAsync(PositionStaging.StateKey("ABC")))!)!;
        Assert.Equal(110m, staging.Stage2EntryPrice);
        Assert.NotNull(staging.Stage2EntryTimestamp);

        await c.ApplyAsync(OrderSide.Buy, "ABC", 30, 120m, new OrderIntent("Add", "ABC", 0m, 100m, 95m, 30m, null, PyramidStage.Stage3Add));
        pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(100m, pos.Quantity);
        Assert.Equal(PyramidStage.Stage3Add, pos.HighestStageFilled);
    }

    [Fact]
    public async Task AddWithoutOpenPosition_IsNotApplied_AndDoesNotThrow()
    {
        using var c = new Ctx();
        await c.ApplyAsync(OrderSide.Buy, "ZZZ", 30, 110m, new OrderIntent("Add", "ZZZ", 0m, 100m, 95m, 30m, null, PyramidStage.Stage2Add));
        Assert.Empty(await c.Positions.GetOpenAsync());
    }

    [Fact]
    public void OrderIntent_OldJsonWithoutStageFields_StillDeserializes()
    {
        const string old = """{"Kind":"Entry","Symbol":"ABC","PivotPrice":1,"SuggestedEntry":2,"SuggestedStop":1,"SuggestedPositionSize":10,"ExitReason":null}""";
        var i = JsonSerializer.Deserialize<OrderIntent>(old)!;
        Assert.Equal("Entry", i.Kind);
        Assert.Null(i.Stage);
        Assert.Equal(0m, i.TotalTargetQuantity);
    }
}
