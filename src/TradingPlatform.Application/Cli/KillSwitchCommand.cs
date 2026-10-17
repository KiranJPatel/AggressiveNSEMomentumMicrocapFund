using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- killswitch status | activate --reason "..." | reset` (spec §6.4).
/// Two things the spec requires that had no entry point before: the MANUAL kill-switch source
/// (KillSwitchSource.Manual existed but nothing could set it) and the manual reset that a latched switch needs
/// (KillSwitchEngine.Reset() had no caller). Both write a RiskEvent audit row; neither can be done from the
/// dashboard — halting/resuming trading is deliberately a CLI-only, human-at-a-terminal action.
/// </summary>
public static class KillSwitchCommand
{
    public static async Task<int> RunAsync(string[] args, IEngineStateRepository engineState, IRiskEventRepository riskEvents, INotificationService? notifier, ILogger logger, CancellationToken cancellationToken = default)
    {
        var action = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        var current = await TradeCycleCommand.LoadStateAsync<KillSwitchState>(engineState, TradeCycleCommand.KillSwitchStateKey, cancellationToken) ?? KillSwitchState.Inactive;

        switch (action)
        {
            case "status":
                Console.WriteLine(current.IsActive
                    ? $"Kill switch: ACTIVE — {current.Source}: {current.Reason} (since {current.ActivatedAt:u}; risk-reducing exits {(current.AllowsRiskReducingExits ? "allowed" : "blocked")})"
                    : "Kill switch: inactive");
                return 0;

            case "activate":
            {
                var reason = ReadFlag(args, "--reason");
                if (string.IsNullOrWhiteSpace(reason))
                {
                    Console.Error.WriteLine("killswitch activate requires --reason \"<why>\" (it is written to the audit log).");
                    return 1;
                }
                var next = KillSwitchEngine.Activate(current, KillSwitchSource.Manual, reason, DateTimeOffset.UtcNow);
                await TradeCycleCommand.SaveStateAsync(engineState, TradeCycleCommand.KillSwitchStateKey, next, cancellationToken);
                if (!current.IsActive)
                {
                    await riskEvents.InsertAsync(new RiskEvent { EventType = "KillSwitchActivated", Description = $"Manual: {reason}", Timestamp = DateTimeOffset.UtcNow, CorrelationId = Guid.NewGuid().ToString() }, cancellationToken);
                    if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.KillSwitch, $"Kill switch ACTIVATED manually — {reason}", cancellationToken);
                    Console.WriteLine($"Kill switch ACTIVATED (Manual): {reason}");
                }
                else Console.WriteLine($"Kill switch was already active ({current.Source}: {current.Reason}); the original trigger is kept.");
                logger.LogWarning("killswitch: activate requested, manual reason '{Reason}'.", reason);
                return 0;
            }

            case "reset":
            {
                if (!current.IsActive) { Console.WriteLine("Kill switch is not active — nothing to reset."); return 0; }
                var next = KillSwitchEngine.Reset();
                await TradeCycleCommand.SaveStateAsync(engineState, TradeCycleCommand.KillSwitchStateKey, next, cancellationToken);
                await TradeCycleCommand.SaveStateAsync(engineState, TradeCycleCommand.KillSwitchResetAtKey, DateTimeOffset.UtcNow, cancellationToken);
                await riskEvents.InsertAsync(new RiskEvent { EventType = "KillSwitchReset", Description = $"Manually reset (was {current.Source}: {current.Reason})", Timestamp = DateTimeOffset.UtcNow, CorrelationId = Guid.NewGuid().ToString() }, cancellationToken);
                if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.KillSwitch, $"Kill switch manually reset (was {current.Source}: {current.Reason}).", cancellationToken);
                Console.WriteLine($"Kill switch RESET (was {current.Source}: {current.Reason}). Re-check reconciliation before the next live cycle.");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown killswitch action '{action}'. Use: status | activate --reason \"...\" | reset");
                return 1;
        }
    }

    private static string? ReadFlag(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
