namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// Signal/order/fill/rejection/stop/drawdown/kill-switch/reconciliation-failure/
/// system-failure notifications (spec §7.2). Console-only in Tranche 0;
/// Telegram/Email/Slack adapters are later tranches.
/// </summary>
public interface INotificationService
{
    Task NotifyAsync(string category, string message, CancellationToken cancellationToken = default);
}
