namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "YahooFinance" (spec §5.2). No magic numbers in code.</summary>
public sealed class YahooFinanceOptions
{
    public const string SectionName = "YahooFinance";

    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com";
    public string ChartPathTemplate { get; set; } = "/v8/finance/chart/{0}";
    public string DefaultSymbolSuffix { get; set; } = ".NS";
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;
    public int InitialBackoffMilliseconds { get; set; } = 500;
    public int MaxBackoffMilliseconds { get; set; } = 8000;

    public int RateLimitRequestsPerMinute { get; set; } = 60;

    /// <summary>Day-over-day % move (on raw Close) flagged as a possible unexplained gap / corporate action (spec §5.3).</summary>
    public decimal ExtremeGapPercentThreshold { get; set; } = 15m;

    /// <summary>
    /// Flagged UNVERIFIED per spec §0.2/§1.3 until confirmed against a live
    /// response from query1.finance.yahoo.com with network access — this
    /// sandbox cannot reach Yahoo (see docs/gap-log.md), so the chart JSON
    /// shape parsed here is implemented from documented/known structure and
    /// tested only against hand-built fixture payloads, never a real response.
    /// </summary>
    public bool AdapterVerifiedAgainstLiveResponse { get; set; } = false;
}
