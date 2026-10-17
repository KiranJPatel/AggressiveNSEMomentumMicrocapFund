using System.Text.Json;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Zerodha;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class LiveOrderSyncTests
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

        public async Task<long> SubmitAsync(OrderSide side, string symbol, decimal qty, string brokerId, string corr, OrderIntent? intent)
        {
            var id = await Orders.InsertAsync(new Order { Symbol = symbol, Side = side, Type = OrderType.Market, State = OrderState.Submitted, Quantity = qty, BrokerOrderId = brokerId, CorrelationId = corr });
            if (intent is not null) await State.SetAsync(OrderIntent.StateKey(corr), JsonSerializer.Serialize(intent));
            return id;
        }

        public Task<OrderSyncResult> SyncAsync(params Order[] brokerOrders) =>
            LiveOrderSync.RunAsync(_ => Task.FromResult<IReadOnlyList<Order>>(brokerOrders), Orders, Positions, Trades, Signals, Risk, State, null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
    }

    private static Order Broker(string id, OrderState st, decimal qty, decimal filled, decimal? avg, string symbol = "ABC", string? reason = null) =>
        new() { Symbol = symbol, BrokerOrderId = id, State = st, Quantity = qty, FilledQuantity = filled, AverageFillPrice = avg, RejectionReason = reason, CorrelationId = "" };

    private static OrderIntent EntryIntent(string symbol = "ABC") => new("Entry", symbol, 100m, 101m, 95m, 50m, null);

    [Fact]
    public async Task FilledEntry_CreatesPositionAtActualFillPrice_AndRecordsFillAndCash()
    {
        using var c = new Ctx();
        var id = await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K1", "corr1", EntryIntent());
        var r = await c.SyncAsync(Broker("K1", OrderState.Filled, 50, 50, 102m));

        Assert.Equal(1, r.Filled);
        Assert.Equal(-(50 * 102m), r.CashDelta);
        var pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(102m, pos.AverageEntryPrice);          // actual fill, not the 101 signal price
        Assert.Equal(7m, pos.RiskPerShare);                  // 102 - 95 stop
        Assert.Equal(50m, pos.Quantity);
        var fill = Assert.Single(await c.Orders.GetFillsAsync(id));
        Assert.Equal(102m, fill.Price);
        Assert.Equal(50m, fill.SlippageAmount);              // |102-101| * 50
        Assert.Equal(OrderState.Filled, (await c.Orders.GetByIdAsync(id))!.State);
    }

    [Fact]
    public async Task SecondSync_DoesNotApplyTheSameFillTwice()
    {
        using var c = new Ctx();
        await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K1", "corr1", EntryIntent());
        await c.SyncAsync(Broker("K1", OrderState.Filled, 50, 50, 102m));
        var again = await c.SyncAsync(Broker("K1", OrderState.Filled, 50, 50, 102m));
        Assert.Equal(0, again.Checked);
        Assert.Equal(1, (await c.Positions.GetOpenAsync()).Count);
    }

    [Fact]
    public async Task FilledExit_ClosesPosition_AndJournalsTradeAtRealExitPrice()
    {
        using var c = new Ctx();
        await c.Positions.UpsertAsync(new Position { Symbol = "ABC", Quantity = 50, AverageEntryPrice = 100m, InitialStop = 95m, CurrentStop = 95m, InitialRiskAmount = 250m, RiskPerShare = 5m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true, OpenedAt = DateTimeOffset.UtcNow.AddDays(-3) });
        await c.SubmitAsync(OrderSide.Sell, "ABC", 50, "K2", "corr2", new OrderIntent("Exit", "ABC", 0m, 100m, 95m, 50m, ExitReason.StopLoss));

        var r = await c.SyncAsync(Broker("K2", OrderState.Filled, 50, 50, 94m));

        Assert.Equal(50 * 94m, r.CashDelta);
        Assert.Empty(await c.Positions.GetOpenAsync());
        var trade = Assert.Single(await c.Trades.GetBySymbolAsync("ABC"));
        Assert.Equal(94m, trade.ExitPrice);
        Assert.Equal(-300m, trade.RealizedPnl);
        Assert.Equal(-1.2m, trade.RMultiple);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
    }

    [Fact]
    public async Task RejectedAtBroker_BecomesTerminal_AndCreatesNoPosition()
    {
        using var c = new Ctx();
        var id = await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K3", "corr3", EntryIntent());
        var r = await c.SyncAsync(Broker("K3", OrderState.Rejected, 50, 0, null, reason: "Insufficient funds"));

        Assert.Equal(1, r.Terminal);
        Assert.Empty(await c.Positions.GetOpenAsync());
        Assert.Equal(OrderState.Rejected, (await c.Orders.GetByIdAsync(id))!.State);
        Assert.Empty(await c.Orders.GetOpenOrdersAsync());
    }

    [Fact]
    public async Task FilledWithoutStoredIntent_DoesNotInventAPosition()
    {
        using var c = new Ctx();
        await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K4", "corr4", null);
        var r = await c.SyncAsync(Broker("K4", OrderState.Filled, 50, 50, 100m));
        Assert.Equal(1, r.Filled);
        Assert.Equal(0m, r.CashDelta);
        Assert.Empty(await c.Positions.GetOpenAsync());
    }

    [Fact]
    public async Task OrderMissingFromBrokerBook_IsCountedAndLeftOpen()
    {
        using var c = new Ctx();
        await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K5", "corr5", EntryIntent());
        var r = await c.SyncAsync(Broker("OTHER", OrderState.Filled, 1, 1, 1m));
        Assert.Equal(1, r.MissingAtBroker);
        Assert.Equal(1, (await c.Orders.GetOpenOrdersAsync()).Count);
    }

    [Fact]
    public async Task StillOpenAtBroker_ChangesNothingButState()
    {
        using var c = new Ctx();
        await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "K6", "corr6", EntryIntent());
        var r = await c.SyncAsync(Broker("K6", OrderState.Open, 50, 0, null));
        Assert.Equal(0, r.Filled);
        Assert.Empty(await c.Positions.GetOpenAsync());
    }

    [Fact]
    public void KiteParser_ReadsFilledQuantityAndAveragePrice()
    {
        const string json = """
            {"status":"success","data":[
              {"order_id":"123","tradingsymbol":"ABC","transaction_type":"BUY","order_type":"MARKET","quantity":50,"filled_quantity":50,"average_price":102.35,"status":"COMPLETE"},
              {"order_id":"124","tradingsymbol":"XYZ","transaction_type":"SELL","order_type":"MARKET","quantity":10,"filled_quantity":0,"average_price":0,"status":"OPEN"}]}
            """;
        var orders = KiteResponseParser.ParseOrders(200, json);
        Assert.Equal(50m, orders[0].FilledQuantity);
        Assert.Equal(102.35m, orders[0].AverageFillPrice);
        Assert.Equal(OrderState.Filled, orders[0].State);
        Assert.Null(orders[1].AverageFillPrice);
    }

    // ---- partial fills ----
    [Fact]
    public async Task PartialEntry_CreatesPositionForFilledShares_ThenGrowsItOnCompletion_WithCorrectSlicePrice()
    {
        using var c = new Ctx();
        var id = await c.SubmitAsync(OrderSide.Buy, "ABC", 100, "P1", "corrP1", EntryIntent());

        // Slice 1: 40 of 100 @ 100 (broker order still OPEN).
        var r1 = await c.SyncAsync(Broker("P1", OrderState.Open, 100, 40, 100m));
        Assert.Equal(1, r1.Filled);
        Assert.Equal(-(40 * 100m), r1.CashDelta);
        var pos = Assert.Single(await c.Positions.GetOpenAsync());
        Assert.Equal(40m, pos.Quantity);
        Assert.Equal(OrderState.PartiallyFilled, (await c.Orders.GetByIdAsync(id))!.State);
        Assert.Equal(1, (await c.Orders.GetOpenOrdersAsync()).Count);   // still being tracked

        // Slice 2: COMPLETE, 100 filled at cumulative average 102 => the NEW 60 shares cost (102*100 - 100*40)/60 = 103.33...
        var r2 = await c.SyncAsync(Broker("P1", OrderState.Filled, 100, 100, 102m));
        Assert.Equal(1, r2.Filled);
        pos = Assert.Single(await c.Positions.GetOpenAsync());          // grown, NOT a second position
        Assert.Equal(100m, pos.Quantity);
        Assert.Equal(102m, Math.Round(pos.AverageEntryPrice, 6));       // weighted average equals the broker's cumulative average
        Assert.Equal(OrderState.Filled, (await c.Orders.GetByIdAsync(id))!.State);
        Assert.Equal(2, (await c.Orders.GetFillsAsync(id)).Count);
        Assert.Empty(await c.Orders.GetOpenOrdersAsync());
    }

    [Fact]
    public async Task ResyncWithUnchangedPartial_DoesNotDoubleCount()
    {
        using var c = new Ctx();
        await c.SubmitAsync(OrderSide.Buy, "ABC", 100, "P2", "corrP2", EntryIntent());
        await c.SyncAsync(Broker("P2", OrderState.Open, 100, 40, 100m));
        var again = await c.SyncAsync(Broker("P2", OrderState.Open, 100, 40, 100m));
        Assert.Equal(0, again.Filled);
        Assert.Equal(0m, again.CashDelta);
        Assert.Equal(40m, Assert.Single(await c.Positions.GetOpenAsync()).Quantity);
    }

    [Fact]
    public async Task CancelledAfterPartialFill_KeepsTheFilledShares()
    {
        using var c = new Ctx();
        var id = await c.SubmitAsync(OrderSide.Buy, "ABC", 100, "P3", "corrP3", EntryIntent());
        var r = await c.SyncAsync(Broker("P3", OrderState.Cancelled, 100, 30, 101m));

        Assert.Equal(1, r.Terminal);
        Assert.Equal(30m, Assert.Single(await c.Positions.GetOpenAsync()).Quantity);   // the 30 real shares are NOT lost
        Assert.Equal(OrderState.Cancelled, (await c.Orders.GetByIdAsync(id))!.State);
        Assert.Empty(await c.Orders.GetOpenOrdersAsync());
    }

    [Fact]
    public async Task PartialExit_ReducesPosition_ThenClosesOnCompletion()
    {
        using var c = new Ctx();
        await c.Positions.UpsertAsync(new Position { Symbol = "ABC", Quantity = 100, AverageEntryPrice = 100m, InitialStop = 95m, CurrentStop = 95m, InitialRiskAmount = 500m, RiskPerShare = 5m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true, OpenedAt = DateTimeOffset.UtcNow.AddDays(-3) });
        await c.SubmitAsync(OrderSide.Sell, "ABC", 100, "P4", "corrP4", new OrderIntent("Exit", "ABC", 0m, 100m, 95m, 100m, ExitReason.StopLoss));

        await c.SyncAsync(Broker("P4", OrderState.Open, 100, 60, 94m));
        Assert.Equal(40m, Assert.Single(await c.Positions.GetOpenAsync()).Quantity);

        await c.SyncAsync(Broker("P4", OrderState.Filled, 100, 100, 94m));
        Assert.Empty(await c.Positions.GetOpenAsync());
        var trades = await c.Trades.GetBySymbolAsync("ABC");
        Assert.Equal(2, trades.Count);
        Assert.Equal(-600m, trades.Sum(t => t.RealizedPnl));   // 100 shares * (94-100)
    }

    [Fact]
    public async Task FilledWithoutIntent_StillRecordsTheFillRow_SoItIsNotReprocessed()
    {
        using var c = new Ctx();
        var id = await c.SubmitAsync(OrderSide.Buy, "ABC", 50, "P5", "corrP5", null);
        await c.SyncAsync(Broker("P5", OrderState.Filled, 50, 50, 100m));
        Assert.Equal(1, (await c.Orders.GetFillsAsync(id)).Count);
        Assert.Empty(await c.Positions.GetOpenAsync());
    }
}
