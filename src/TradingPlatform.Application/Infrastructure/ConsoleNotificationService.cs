using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Infrastructure;

/// <summary>
/// The only INotificationService implementation today (spec §7.2: "console
/// now; Telegram/Email/Slack adapters later"). Every notification is ALWAYS
/// written to the structured log (so it lands in the rolling file and the
/// SQLite SystemEvents sink) regardless of channel flags, and is echoed to
/// the console when NotificationOptions.ConsoleEnabled. Critical categories
/// (kill switch, reconciliation failure, system failure, drawdown) log at
/// Warning/Error and are echoed to stderr so they survive stdout redirection.
///
/// Telegram/Email/Slack: no adapters exist yet. If one of those flags is set
/// to true this service says so loudly, once, rather than letting an operator
/// believe alerts are being delivered somewhere they are not — an alert
/// channel that silently does nothing is worse than no channel.
/// </summary>
public sealed class ConsoleNotificationService : INotificationService
{
    private readonly NotificationOptions _options;
    private readonly ILogger _logger;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private int _unimplementedChannelWarned;

    public ConsoleNotificationService(IOptions<NotificationOptions> options, ILogger logger, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _options = options.Value;
        _logger = logger;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    public Task NotifyAsync(string category, string message, CancellationToken cancellationToken = default)
    {
        category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        var critical = NotificationCategories.IsCritical(category);

        WarnOnceIfUnimplementedChannelEnabled();

        if (critical) _logger.LogWarning("NOTIFY [{Category}] {Message}", category, message);
        else _logger.LogInformation("NOTIFY [{Category}] {Message}", category, message);

        if (_options.ConsoleEnabled)
        {
            var line = $"[{DateTimeOffset.Now:HH:mm:ss}] [{category.ToUpperInvariant()}] {message}";
            (critical ? _stderr : _stdout).WriteLine(line);
        }
        return Task.CompletedTask;
    }

    private void WarnOnceIfUnimplementedChannelEnabled()
    {
        if (!(_options.TelegramEnabled || _options.EmailEnabled || _options.SlackEnabled)) return;
        if (Interlocked.Exchange(ref _unimplementedChannelWarned, 1) == 1) return;
        _logger.LogError("Notification:Telegram/Email/Slack is enabled in config but NO such adapter is implemented — alerts are going to console and log only.");
        _stderr.WriteLine("[WARNING] Notification channel (Telegram/Email/Slack) is enabled in config but not implemented — alerts go to console/log only.");
    }
}
