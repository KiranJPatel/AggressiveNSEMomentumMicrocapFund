using System.Security.Cryptography;
using System.Text;

namespace TradingPlatform.Application.Api;

/// <summary>
/// Access control for the REST API / dashboard (`serve`). The API exposes positions, orders and P&amp;L, and until now had no
/// authentication at all.
///
///  - If the environment variable <see cref="ApiKeyEnvVar"/> is set, every `/api/*` request must carry it in the `X-Api-Key` header
///    (static files — the dashboard shell, which contains no account data — stay open so the browser can load the page and ask for the key).
///  - If the server is bound to anything other than loopback and NO key is set, `serve` refuses to start unless explicitly told
///    `--allow-unauthenticated`: publishing a port (Docker, a LAN bind) must not silently publish an account.
///
/// A shared secret over plain HTTP is only as safe as the network path: use a TLS-terminating reverse proxy or an SSH tunnel for anything
/// beyond localhost. This is a gate against casual/accidental exposure, not a substitute for network isolation.
/// </summary>
public static class ApiSecurity
{
    public const string ApiKeyEnvVar = "TRADING_API_KEY";
    public const string HeaderName = "X-Api-Key";
    public const string DefaultUrls = "http://localhost:5000";

    /// <summary>Constant-time comparison (hashes first, so length differences do not leak either). No configured key means no gate.</summary>
    public static bool IsAuthorized(string? configuredKey, string? presentedKey)
    {
        if (string.IsNullOrEmpty(configuredKey)) return true;
        if (string.IsNullOrEmpty(presentedKey)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// True only if every URL in a semicolon-separated ASPNETCORE_URLS value binds a loopback host. Anything unparseable, wildcard
    /// (`+`, `*`, `0.0.0.0`, `[::]`) or non-loopback counts as exposed — the safe direction to be wrong in.
    /// </summary>
    public static bool IsLoopbackOnly(string? urls)
    {
        var list = (string.IsNullOrWhiteSpace(urls) ? DefaultUrls : urls).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (list.Length == 0) return true;
        foreach (var raw in list)
        {
            // Kestrel accepts wildcard hosts that Uri cannot parse ("http://+:5000", "http://*:5000"): substitute a placeholder to read the host.
            var candidate = raw.Replace("://+", "://wildcard.invalid").Replace("://*", "://wildcard.invalid");
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Null when it is fine to start; otherwise the reason `serve` must refuse.</summary>
    public static string? StartupViolation(string? urls, string? configuredKey, bool allowUnauthenticated)
    {
        if (!string.IsNullOrEmpty(configuredKey) || IsLoopbackOnly(urls) || allowUnauthenticated) return null;
        return $"Refusing to start: the API would listen on a non-loopback address ({(string.IsNullOrWhiteSpace(urls) ? DefaultUrls : urls)}) with no access key — " +
               $"anyone who can reach the port could read your positions and P&L. Set {ApiKeyEnvVar}, or bind to localhost, or pass --allow-unauthenticated if the network is already isolated.";
    }
}
