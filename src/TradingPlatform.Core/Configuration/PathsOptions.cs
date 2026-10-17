namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Paths" (spec §7.5). Configurable directories.</summary>
public sealed class PathsOptions
{
    public const string SectionName = "Paths";

    public string Data { get; set; } = "data";
    public string Logs { get; set; } = "logs";
    public string Config { get; set; } = "config";
    public string Reports { get; set; } = "reports";
    public string Backtests { get; set; } = "backtests";
    public string Exports { get; set; } = "exports";
}
