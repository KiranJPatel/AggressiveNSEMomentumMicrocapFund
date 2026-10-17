using TradingPlatform.Core.Enums;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class KillSwitchEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 10, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Inactive_AllowsBothSides()
    {
        var state = KillSwitchState.Inactive;
        Assert.True(KillSwitchEngine.AllowsOrder(state, OrderSide.Buy));
        Assert.True(KillSwitchEngine.AllowsOrder(state, OrderSide.Sell));
    }

    [Fact]
    public void Activate_BlocksBuy_ButAllowsSell_ForRiskDrivenSource()
    {
        var state = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Drawdown, "40% drawdown", Now);

        Assert.True(state.IsActive);
        Assert.False(KillSwitchEngine.AllowsOrder(state, OrderSide.Buy));
        Assert.True(KillSwitchEngine.AllowsOrder(state, OrderSide.Sell));
    }

    [Fact]
    public void Activate_DataError_BlocksBothSides_NoExitCarveOut()
    {
        var state = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.DataError, "corrupt bar data", Now);

        Assert.False(KillSwitchEngine.AllowsOrder(state, OrderSide.Buy));
        Assert.False(KillSwitchEngine.AllowsOrder(state, OrderSide.Sell), "A data-integrity problem must not be trusted to safely inform even an exit order.");
    }

    [Fact]
    public void Activate_ConnectivityFailure_BlocksBothSides_NoExitCarveOut()
    {
        var state = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.ConnectivityFailure, "broker unreachable", Now);
        Assert.False(KillSwitchEngine.AllowsOrder(state, OrderSide.Sell));
    }

    [Fact]
    public void AlreadyActive_SecondActivation_KeepsOriginalSourceAndReason()
    {
        var first = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Drawdown, "drawdown halt", Now);
        var second = KillSwitchEngine.Activate(first, KillSwitchSource.BrokerError, "unrelated broker error", Now.AddMinutes(5));

        Assert.Equal(KillSwitchSource.Drawdown, second.Source);
        Assert.Equal("drawdown halt", second.Reason);
        Assert.Equal(first.ActivatedAt, second.ActivatedAt);
    }

    [Fact]
    public void Reset_ClearsToInactive()
    {
        var active = KillSwitchEngine.Activate(KillSwitchState.Inactive, KillSwitchSource.Manual, "operator halt", Now);
        var reset = KillSwitchEngine.Reset();

        Assert.False(reset.IsActive);
        Assert.True(KillSwitchEngine.AllowsOrder(reset, OrderSide.Buy));
    }
}
