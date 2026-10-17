namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Notification" (spec §7.2). Console is the only channel wired in Tranche 0.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    public bool ConsoleEnabled { get; set; } = true;
    public bool TelegramEnabled { get; set; } = false;
    public bool EmailEnabled { get; set; } = false;
    public bool SlackEnabled { get; set; } = false;
}
