using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// Tests for the wiring added after the spec-vs-code audit: notification service, kill-switch command,
/// repeated-rejection history feed, and fill-price/quantity capture on the order model.
/// </summary>
public class Tranche22WiringTests
{
    private static ConsoleNotificationService Notifier(NotificationOptions o, StringWriter outW, StringWriter errW) =>
        new(Options.Create(o), NullLogger.Instance, outW, errW);

    [Fact]
    public async Task Notifier_NonCritical_GoesToStdout()
    {
        var o = new StringWriter(); var e = new StringWriter();
        await Notifier(new NotificationOptions { ConsoleEnabled = true }, o, e).NotifyAsync(NotificationCategories.Fill, "BUY 10 ABC filled.");
        Assert.True(o.ToString().Contains("[FILL] BUY 10 ABC filled."));
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Notifier_Critical_GoesToStderr()
    {
        var o = new StringWriter(); var e = new StringWriter();
        await Notifier(new NotificationOptions { ConsoleEnabled = true }, o, e).NotifyAsync(NotificationCategories.KillSwitch, "halted");
        Assert.True(e.ToString().Contains("[KILLSWITCH] halted"));
        Assert.Equal("", o.ToString());
    }

    [Fact]
    public async Task Notifier_ConsoleDisabled_WritesNothing()
    {
        var o = new StringWriter(); var e = new StringWriter();
        await Notifier(new NotificationOptions { ConsoleEnabled = false }, o, e).NotifyAsync(NotificationCategories.SystemFailure, "x");
        Assert.Equal("", o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public async Task Notifier_UnimplementedChannelEnabled_WarnsExactlyOnce()
    {
        var o = new StringWriter(); var e = new StringWriter();
        var n = Notifier(new NotificationOptions { ConsoleEnabled = true, TelegramEnabled = true }, o, e);
        await n.NotifyAsync(NotificationCategories.Fill, "a");
        await n.NotifyAsync(NotificationCategories.Fill, "b");
        var warnings = e.ToString().Split('\n').Count(l => l.Contains("not implemented"));
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void Categories_CriticalSet_IsExactlyTheCapitalAtRiskOnes()
    {
        Assert.True(NotificationCategories.IsCritical(NotificationCategories.KillSwitch));
        Assert.True(NotificationCategories.IsCritical(NotificationCategories.ReconciliationFailure));
        Assert.True(NotificationCategories.IsCritical(NotificationCategories.SystemFailure));
        Assert.True(NotificationCategories.IsCritical(NotificationCategories.Drawdown));
        Assert.False(NotificationCategories.IsCritical(NotificationCategories.Fill));
        Assert.False(NotificationCategories.IsCritical(NotificationCategories.Signal));
    }

    [Fact]
    public async Task KillSwitchCommand_ActivateThenReset_RoundTripsAndAudits()
    {
        using var host = CliTestHost.Create();
        var state = host.Repo<IEngineStateRepository>(); var risk = host.Repo<IRiskEventRepository>();

        Assert.Equal(1, await KillSwitchCommand.RunAsync(new[] { "killswitch", "activate" }, state, risk, null, CliTestHost.Logger)); // reason required

        Assert.Equal(0, await KillSwitchCommand.RunAsync(new[] { "killswitch", "activate", "--reason", "testing" }, state, risk, null, CliTestHost.Logger));
        var json = await state.GetAsync(TradeCycleCommand.KillSwitchStateKey);
        var ks = JsonSerializer.Deserialize<KillSwitchState>(json!)!;
        Assert.True(ks.IsActive);
        Assert.Equal(KillSwitchSource.Manual, ks.Source);

        Assert.Equal(0, await KillSwitchCommand.RunAsync(new[] { "killswitch", "reset" }, state, risk, null, CliTestHost.Logger));
        ks = JsonSerializer.Deserialize<KillSwitchState>((await state.GetAsync(TradeCycleCommand.KillSwitchStateKey))!)!;
        Assert.False(ks.IsActive);
        Assert.NotNull(await state.GetAsync(TradeCycleCommand.KillSwitchResetAtKey));
    }

    [Fact]
    public async Task KillSwitchCommand_UnknownAction_ReturnsError()
    {
        using var host = CliTestHost.Create();
        Assert.Equal(1, await KillSwitchCommand.RunAsync(new[] { "killswitch", "bogus" }, host.Repo<IEngineStateRepository>(), host.Repo<IRiskEventRepository>(), null, CliTestHost.Logger));
    }

    private static async Task InsertOrderAsync(IOrderRepository repo, OrderState state, DateTimeOffset createdAt) =>
        await repo.InsertAsync(new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, State = state, Quantity = 1, CorrelationId = Guid.NewGuid().ToString(), CreatedAt = createdAt, UpdatedAt = createdAt });

    [Fact]
    public async Task RejectionHistory_IsOldestFirst_AndOnlyTerminalOutcomes()
    {
        using var host = CliTestHost.Create();
        var orders = host.Repo<IOrderRepository>(); var state = host.Repo<IEngineStateRepository>();
        var t0 = DateTimeOffset.UtcNow.AddHours(-5);
        await InsertOrderAsync(orders, OrderState.Filled, t0);
        await InsertOrderAsync(orders, OrderState.Open, t0.AddMinutes(1));      // not evidence either way
        await InsertOrderAsync(orders, OrderState.Rejected, t0.AddMinutes(2));
        await InsertOrderAsync(orders, OrderState.Failed, t0.AddMinutes(3));

        var h = await TradeCycleCommand.BuildRecentRejectionHistoryAsync(orders, state, default);
        Assert.Equal(3, h.Count);
        Assert.False(h[0]); Assert.True(h[1]); Assert.True(h[2]);
    }

    [Fact]
    public async Task RejectionHistory_IgnoresOrdersBeforeLastManualReset()
    {
        using var host = CliTestHost.Create();
        var orders = host.Repo<IOrderRepository>(); var state = host.Repo<IEngineStateRepository>();
        // SqliteOrderRepository.InsertAsync stamps CreatedAt itself, so ordering is established with real delays.
        var t0 = DateTimeOffset.UtcNow;
        await InsertOrderAsync(orders, OrderState.Rejected, t0);
        await InsertOrderAsync(orders, OrderState.Rejected, t0);
        await Task.Delay(60);
        await state.SetAsync(TradeCycleCommand.KillSwitchResetAtKey, JsonSerializer.Serialize(DateTimeOffset.UtcNow));
        await Task.Delay(60);
        await InsertOrderAsync(orders, OrderState.Filled, t0);

        var h = await TradeCycleCommand.BuildRecentRejectionHistoryAsync(orders, state, default);
        Assert.Equal(1, h.Count);
        Assert.False(h[0]);
    }

    [Fact]
    public async Task SlippageHistory_ConvertsRupeeSlippageToBpsOfFillNotional()
    {
        using var host = CliTestHost.Create();
        var orders = host.Repo<IOrderRepository>(); var state = host.Repo<IEngineStateRepository>();
        var id = await orders.InsertAsync(new Order { Symbol = "ABC", Side = OrderSide.Buy, Type = OrderType.Market, State = OrderState.Filled, Quantity = 100, CorrelationId = "c1" });
        // 100 sh @ 200 = 20,000 notional; 100 rupees of slippage = 50 bps.
        await orders.RecordFillAsync(new Fill { OrderId = id, Symbol = "ABC", Quantity = 100, Price = 200m, SlippageAmount = 100m, Commission = 5m, FilledAt = DateTimeOffset.UtcNow });
        await InsertOrderAsync(orders, OrderState.Rejected, DateTimeOffset.UtcNow); // not a fill: must not appear

        var bps = await TradeCycleCommand.BuildRecentSlippageHistoryAsync(orders, state, default);
        Assert.Equal(1, bps.Count);
        Assert.Equal(50m, bps[0]);
    }

    [Fact]
    public async Task Fills_RoundTripThroughRepository()
    {
        using var host = CliTestHost.Create();
        var orders = host.Repo<IOrderRepository>();
        var id = await orders.InsertAsync(new Order { Symbol = "XYZ", Side = OrderSide.Sell, Type = OrderType.Market, State = OrderState.Filled, Quantity = 10, CorrelationId = "c2" });
        await orders.RecordFillAsync(new Fill { OrderId = id, Symbol = "XYZ", Quantity = 10, Price = 95.5m, SlippageAmount = 1.5m, Commission = 2.25m, FilledAt = DateTimeOffset.UtcNow });
        var f = Assert.Single(await orders.GetFillsAsync(id));
        Assert.Equal(95.5m, f.Price); Assert.Equal(2.25m, f.Commission); Assert.Equal(10m, f.Quantity);
    }
}
