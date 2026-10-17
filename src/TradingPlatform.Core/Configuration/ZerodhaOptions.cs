namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "Zerodha" (spec §6.2). ApiKey/ApiSecret/AccessToken
/// are never stored here directly — these hold the *names* of environment
/// variables to read from, per the secrets-handling requirement in spec §7.5.
/// </summary>
public sealed class ZerodhaOptions
{
    public const string SectionName = "Zerodha";

    public string BaseUrl { get; set; } = "https://api.kite.trade";
    public string LoginBaseUrl { get; set; } = "https://kite.zerodha.com";
    public string ApiKeyEnvVar { get; set; } = "ZERODHA_API_KEY";
    public string ApiSecretEnvVar { get; set; } = "ZERODHA_API_SECRET";
    public string AccessTokenEnvVar { get; set; } = "ZERODHA_ACCESS_TOKEN";
    public int RequestTimeoutSeconds { get; set; } = 15;

    public int MaxRetries { get; set; } = 3;
    public int InitialBackoffMilliseconds { get; set; } = 500;
    public int MaxBackoffMilliseconds { get; set; } = 8000;

    /// <summary>
    /// Kite's documented order-placement limit (spec §6.2's "handle...
    /// rate limiting"): 10 requests/second AND 200 orders/minute — this
    /// field is the per-minute figure; the per-second figure isn't
    /// separately modeled since SlidingWindowRateLimiter (Tranche 2)
    /// already self-throttles to whatever single window is configured,
    /// and the tighter per-minute cap is the one actually binding for a
    /// system this size (a handful of orders per day, never a rapid
    /// burst). UNVERIFIED — see AdapterVerifiedAgainstLiveDocs.
    /// </summary>
    public int OrderRateLimitPerMinute { get; set; } = 200;

    /// <summary>Kite's documented "all other endpoints" limit (positions, margins, order status, etc.) — 10 requests/second, expressed here as a per-minute ceiling for the same SlidingWindowRateLimiter reuse reason as above. UNVERIFIED.</summary>
    public int GeneralRateLimitPerMinute { get; set; } = 600;

    /// <summary>Flagged UNVERIFIED per spec §0.2/§6.2 until confirmed against live docs.</summary>
    public bool AdapterVerifiedAgainstLiveDocs { get; set; } = false;
}
