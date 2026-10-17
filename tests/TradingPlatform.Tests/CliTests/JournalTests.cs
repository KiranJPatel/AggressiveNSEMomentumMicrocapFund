using System.Text.Json;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class JournalTests
{
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

        public async Task<decimal> ApplyAsync(OrderSide side, string sym, decimal qty, decimal price, decimal commission, OrderIntent intent, Position? exitPos = null)
        {
            var o = new Order { Symbol = sym, Side = side, Type = OrderType.Market, State = OrderState.Filled, Quantity = qty, CorrelationId = Guid.NewGuid().ToString(),
                                FilledQuantity = qty, AverageFillPrice = price, FillCommission = commission };
            var id = await Orders.InsertAsync(o);
            return await FillApplier.ApplyAsync(o, id, intent, exitPos, Orders, Positions, Trades, Signals, Risk, State, default);
        }
    }

    private static HistoricalBar Bar(int day, decimal h, decimal l) =>
        new() { Symbol = "ABC", Timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day), Open = l, High = h, Low = l, Close = h, Volume = 1, Source = "t" };

    [Fact]
    public void Excursions_AreInR_UsingBarsFromEntryDateOnward_LikeTheBacktest()
    {
        var p = new Position { Symbol = "ABC", AverageEntryPrice = 100m, RiskPerShare = 5m, OpenedAt = new DateTimeOffset(2026, 9, 3, 9, 30, 0, TimeSpan.Zero), IsOpen = true };
        var bars = new[] { Bar(0, 200, 10), /* before entry: must be ignored */ Bar(2, 104, 98), Bar(3, 115, 101), Bar(4, 110, 96) };
        var (mfe, mae) = IntentFactory.Excursions(p, bars);
        Assert.Equal(3m, mfe);     // (115-100)/5
        Assert.Equal(-0.8m, mae);  // (96-100)/5
    }

    [Fact]
    public void Excursions_NeverNegativeMfe_NorPositiveMae_WhenPriceNeverLeftOneSideOfEntry()
    {
        var p = new Position { Symbol = "ABC", AverageEntryPrice = 100m, RiskPerShare = 5m, OpenedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), IsOpen = true };
        var (mfeDown, maeDown) = IntentFactory.Excursions(p, new[] { Bar(2, 99, 90), Bar(3, 98, 88) });   // only ever below entry
        Assert.Equal(0m, mfeDown); Assert.Equal(-2.4m, maeDown);
        var (mfeUp, maeUp) = IntentFactory.Excursions(p, new[] { Bar(2, 112, 101), Bar(3, 110, 103) });   // only ever above entry
        Assert.Equal(2.4m, mfeUp); Assert.Equal(0m, maeUp);
    }

    [Fact]
    public void Excursions_ZeroWhenNoRiskOrNoBars_NeverInvented()
    {
        var p = new Position { Symbol = "ABC", AverageEntryPrice = 100m, RiskPerShare = 0m, OpenedAt = DateTimeOffset.UtcNow, IsOpen = true };
        Assert.Equal((0m, 0m), IntentFactory.Excursions(p, new[] { Bar(0, 110, 90) }));
        p.RiskPerShare = 5m;
        Assert.Equal((0m, 0m), IntentFactory.Excursions(p, new List<HistoricalBar>()));
    }

    [Fact]
    public async Task ClosedTrade_CarriesEntryScores_Excursions_AndBothLegsCommission()
    {
        using var c = new Ctx();
        await c.ApplyAsync(OrderSide.Buy, "ABC", 100, 100m, 20m, new OrderIntent("Entry", "ABC", 99m, 100m, 95m, 100m, null, PyramidStage.Stage1Initial, 100m, 72, 81.5m, 1m));
        var pos = Assert.Single(await c.Positions.GetOpenAsync());

        await c.ApplyAsync(OrderSide.Sell, "ABC", 100, 110m, 15m, new OrderIntent("Exit", "ABC", 0m, 100m, 95m, 100m, ExitReason.TrailingStopHit, MfeR: 3m, MaeR: -0.4m), pos);

        var t = Assert.Single(await c.Trades.GetBySymbolAsync("ABC"));
        Assert.Equal(72, t.RegimeScoreAtEntry);
        Assert.Equal(81.5m, t.StockScoreAtEntry);
        Assert.Equal(1m, t.RiskAtEntryPercent);
        Assert.Equal(3m, t.MaximumFavorableExcursion);
        Assert.Equal(-0.4m, t.MaximumAdverseExcursion);
        Assert.Equal(100 * 10m - 15m - 20m, t.RealizedPnl);   // gross 1000 - exit 15 - entry 20 = 965: reconciles with cash
    }

    [Fact]
    public async Task PartialExit_TakesProportionalEntryCommission_AndLeavesTheRestForTheNextExit()
    {
        using var c = new Ctx();
        await c.ApplyAsync(OrderSide.Buy, "ABC", 100, 100m, 20m, new OrderIntent("Entry", "ABC", 99m, 100m, 95m, 100m, null, PyramidStage.Stage1Initial, 100m));
        var pos = Assert.Single(await c.Positions.GetOpenAsync());

        await c.ApplyAsync(OrderSide.Sell, "ABC", 25, 110m, 0m, new OrderIntent("Exit", "ABC", 0m, 100m, 95m, 25m, ExitReason.TrailingStopHit), pos);
        var first = (await c.Trades.GetBySymbolAsync("ABC"))[0];
        Assert.Equal(25 * 10m - 5m, first.RealizedPnl);        // 25% of the 20 entry commission = 5

        pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(75m, pos.Quantity);
        await c.ApplyAsync(OrderSide.Sell, "ABC", 75, 110m, 0m, new OrderIntent("Exit", "ABC", 0m, 100m, 95m, 75m, ExitReason.TrailingStopHit), pos);
        var all = await c.Trades.GetBySymbolAsync("ABC");
        Assert.Equal(2, all.Count);
        Assert.Equal(100 * 10m - 20m, all.Sum(t => t.RealizedPnl));   // total entry commission charged exactly once
    }

    [Fact]
    public async Task PyramidAdds_AccumulateEntryCommission_ForStage2And3()
    {
        using var c = new Ctx();
        await c.ApplyAsync(OrderSide.Buy, "ABC", 40, 100m, 10m, new OrderIntent("Entry", "ABC", 99m, 100m, 95m, 100m, null, PyramidStage.Stage1Initial, 100m));
        await c.ApplyAsync(OrderSide.Buy, "ABC", 30, 110m, 6m, new OrderIntent("Add", "ABC", 0m, 100m, 95m, 30m, null, PyramidStage.Stage2Add));
        await c.ApplyAsync(OrderSide.Buy, "ABC", 30, 115m, 4m, new OrderIntent("Add", "ABC", 0m, 100m, 95m, 30m, null, PyramidStage.Stage3Add));
        var staging = JsonSerializer.Deserialize<PositionStaging>((await c.State.GetAsync(PositionStaging.StateKey("ABC")))!)!;
        Assert.Equal(20m, staging.EntryCommission);
        Assert.NotNull(staging.Stage2EntryPrice);
    }

    [Fact]
    public void OldStagingJson_WithoutJournalFields_StillDeserializes()
    {
        const string old = """{"TotalTargetQuantity":100,"Stage1EntryPrice":100,"Stage1EntryTimestamp":"2026-10-01T00:00:00+00:00","Stage2EntryPrice":null,"Stage2EntryTimestamp":null}""";
        var s = JsonSerializer.Deserialize<PositionStaging>(old)!;
        Assert.Equal(0m, s.EntryCommission);
        Assert.Equal(0, s.RegimeScoreAtEntry);
    }
}
