using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Execution.Orchestration;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class LiveModeGateTests
{
    [Fact]
    public void CanPlaceLiveOrders_RequiresBothModeLive_AndAllowLiveTrading()
    {
        Assert.True(LiveModeGate.CanPlaceLiveOrders(new LiveTradingOptions { Mode = TradingMode.Live, AllowLiveTrading = true }));
    }

    [Fact]
    public void CanPlaceLiveOrders_ModeLive_WithoutAllowFlag_IsFalse()
    {
        Assert.False(LiveModeGate.CanPlaceLiveOrders(new LiveTradingOptions { Mode = TradingMode.Live, AllowLiveTrading = false }));
    }

    [Fact]
    public void CanPlaceLiveOrders_AllowFlagSet_WithoutModeLive_IsFalse()
    {
        Assert.False(LiveModeGate.CanPlaceLiveOrders(new LiveTradingOptions { Mode = TradingMode.Paper, AllowLiveTrading = true }));
    }

    [Fact]
    public void DefaultOptions_NeverAllowLiveOrders()
    {
        Assert.False(LiveModeGate.CanPlaceLiveOrders(new LiveTradingOptions()));
    }

    [Fact]
    public void EffectiveExecutionMode_LiveWithoutAllowFlag_FallsBackToPaper_NotSilentlyInert()
    {
        var mode = LiveModeGate.EffectiveExecutionMode(new LiveTradingOptions { Mode = TradingMode.Live, AllowLiveTrading = false });
        Assert.Equal(TradingMode.Paper, mode);
    }

    [Fact]
    public void EffectiveExecutionMode_LiveWithAllowFlag_StaysLive()
    {
        var mode = LiveModeGate.EffectiveExecutionMode(new LiveTradingOptions { Mode = TradingMode.Live, AllowLiveTrading = true });
        Assert.Equal(TradingMode.Live, mode);
    }

    [Fact]
    public void EffectiveExecutionMode_DryRun_PassesThroughUnchanged()
    {
        var mode = LiveModeGate.EffectiveExecutionMode(new LiveTradingOptions { Mode = TradingMode.DryRun });
        Assert.Equal(TradingMode.DryRun, mode);
    }
}
