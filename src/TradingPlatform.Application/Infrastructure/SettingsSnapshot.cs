using Microsoft.Extensions.Configuration;

namespace TradingPlatform.Application.Infrastructure;

/// <summary>
/// Read-only view of the effective configuration for the dashboard's Settings page (GET /api/settings).
/// Strictly read-only by design: changing capital, risk limits or the live-trading gate from a browser is a
/// safety decision that stays in config/appsettings.json + the CLI. Secrets are never emitted: the Zerodha
/// section reports only whether each credential environment variable is SET (never its value), and any
/// configuration key that looks like a credential (Secret/Password/Token/ApiKey) is dropped unless it is an
/// "...EnvVar" setting (the NAME of an environment variable, which is configuration, not a secret).
/// </summary>
public static class SettingsSnapshot
{
    public sealed record SettingItem(string Key, string Value);
    public sealed record SettingGroup(string Name, string Description, IReadOnlyList<SettingItem> Items);
    public sealed record Snapshot(IReadOnlyList<SettingGroup> Groups, IReadOnlyList<SettingItem> Credentials, string Mode, bool LiveTradingAllowed);

    // Ordered for the page: the safety-relevant sections first.
    private static readonly (string Section, string Title, string Description)[] Sections =
    {
        ("LiveTrading", "Live trading gate", "Mode and the master live-trading switch. Live orders require Mode=Live AND AllowLiveTrading=true."),
        ("Portfolio", "Portfolio", "Capital and position-level concentration limits."),
        ("Risk", "Risk", "Per-trade risk, portfolio heat, loss limits and drawdown tiers."),
        ("Strategy", "Strategy", "Pyramiding stages, breakout rules, trailing-stop triggers and score weights."),
        ("MarketRegime", "Market regime", "Regime thresholds, exposure caps and component weights."),
        ("Exit", "Exits", "Momentum-failure, distribution and trailing-stop parameters."),
        ("Scanner", "Scanner", "Schedule, universe filters, surveillance window and data-quality gates."),
        ("Realtime", "Real-time data", "Live-quote overlay for paper/live while the market is open: provider, freshness and sanity limits."),
        ("Backtest", "Backtesting", "Slippage, cost model and validation windows."),
        ("Notification", "Notifications", "Alert channels. Only console is implemented."),
        ("Benchmark", "Benchmarks", "Index symbols used for relative strength and regime."),
        ("Database", "Database", "SQLite location."),
        ("Paths", "Paths", "Working folders."),
    };

    private static bool LooksLikeCredential(string key)
    {
        if (key.EndsWith("EnvVar", StringComparison.OrdinalIgnoreCase)) return false;
        var k = key.ToLowerInvariant();
        return k.Contains("secret") || k.Contains("password") || k.Contains("token") || k.Contains("apikey");
    }

    private static void Flatten(IConfigurationSection section, string prefix, List<SettingItem> into)
    {
        foreach (var child in section.GetChildren())
        {
            var key = prefix.Length == 0 ? child.Key : $"{prefix}.{child.Key}";
            if (LooksLikeCredential(child.Key)) continue;
            if (child.Value is not null) into.Add(new SettingItem(key, child.Value));
            else Flatten(child, key, into);
        }
    }

    public static Snapshot Build(IConfiguration configuration, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var groups = new List<SettingGroup>();
        foreach (var (section, title, description) in Sections)
        {
            var items = new List<SettingItem>();
            Flatten(configuration.GetSection(section), "", items);
            if (items.Count > 0) groups.Add(new SettingGroup(title, description, items));
        }

        var credentials = new List<SettingItem>();
        foreach (var (label, envKey) in new[] { ("API key", "ApiKeyEnvVar"), ("API secret", "ApiSecretEnvVar"), ("Access token", "AccessTokenEnvVar") })
        {
            var envName = configuration[$"Zerodha:{envKey}"];
            var present = !string.IsNullOrWhiteSpace(envName) && !string.IsNullOrWhiteSpace(getEnvironmentVariable(envName));
            credentials.Add(new SettingItem($"Zerodha {label}", present ? "set" : "not set"));
        }

        var mode = configuration["LiveTrading:Mode"] ?? "Paper";
        var allowed = bool.TryParse(configuration["LiveTrading:AllowLiveTrading"], out var a) && a;
        return new Snapshot(groups, credentials, mode, allowed && string.Equals(mode, "Live", StringComparison.OrdinalIgnoreCase));
    }
}
