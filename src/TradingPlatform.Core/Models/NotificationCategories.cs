namespace TradingPlatform.Core.Models;

/// <summary>
/// The notification categories named by spec §7.2: signal / order / fill /
/// rejection / stop / drawdown / kill-switch / reconciliation-failure /
/// system-failure. Plain string constants (not an enum) because
/// INotificationService.NotifyAsync takes a category string so future
/// Telegram/Email/Slack adapters can route on it without a Core change.
/// </summary>
public static class NotificationCategories
{
    public const string Signal = "Signal";
    public const string Order = "Order";
    public const string Fill = "Fill";
    public const string Rejection = "Rejection";
    public const string Stop = "Stop";
    public const string Drawdown = "Drawdown";
    public const string KillSwitch = "KillSwitch";
    public const string ReconciliationFailure = "ReconciliationFailure";
    public const string SystemFailure = "SystemFailure";

    /// <summary>Categories that must never be dropped or buried: they indicate capital is at risk or trading has halted.</summary>
    public static bool IsCritical(string category) =>
        category is KillSwitch or ReconciliationFailure or SystemFailure or Drawdown;
}
