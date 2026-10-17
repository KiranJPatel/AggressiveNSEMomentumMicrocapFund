namespace TradingPlatform.Core.Enums;

/// <summary>Kill-switch trigger sources (spec §6.4).</summary>
public enum KillSwitchSource
{
    Manual = 0,
    Drawdown = 1,
    DailyLossLimit = 2,
    BrokerError = 3,
    DataError = 4,
    ReconciliationError = 5,
    RepeatedOrderRejection = 6,
    AbnormalSlippage = 7,
    ConnectivityFailure = 8
}
