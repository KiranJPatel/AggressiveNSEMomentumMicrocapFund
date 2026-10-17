using System.Security.Cryptography;
using System.Text;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Kite Connect v3 login-flow helpers (spec §6.2). UNVERIFIED — see
/// ZerodhaOptions.AdapterVerifiedAgainstLiveDocs and docs/gap-log.md: this
/// sandbox has no network access to kite.trade, so this is implemented
/// directly against the publicly documented v3 contract (retrieved via
/// web search of kite.trade/docs/connect/v3/ — login flow, session
/// exchange, and the SHA-256 checksum construction), NOT verified against
/// a live account or a captured real request/response.
///
/// Documented flow: the user is sent to Kite's login page with the app's
/// api_key; after login Kite redirects back with a request_token; the app
/// exchanges (api_key, request_token, checksum) for an access_token via
/// POST /session/token, where checksum = SHA256(api_key + request_token +
/// api_secret) as a lowercase hex string.
/// </summary>
public static class KiteChecksumHelper
{
    public static string BuildLoginUrl(string apiKey) => $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(apiKey)}";

    public static string ComputeChecksum(string apiKey, string requestToken, string apiSecret)
    {
        var input = apiKey + requestToken + apiSecret;
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
