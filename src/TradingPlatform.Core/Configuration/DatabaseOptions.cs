namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Database" (spec §7.2). Hand-rolled SQLite access (see docs/gap-log.md).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string DatabasePath { get; set; } = "data/tradingplatform.db";
    public int CommandTimeoutSeconds { get; set; } = 30;
}
