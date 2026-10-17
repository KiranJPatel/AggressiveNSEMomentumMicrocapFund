using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Execution.Zerodha;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `zerodha-login [--request-token T]` — the daily Kite Connect session step the other commands depend on.
///
/// Kite access tokens expire every day (around 06:00 IST), and nothing else in the platform can obtain one: `live`, `reconcile` and the
/// Kite quote overlay only READ ZERODHA_ACCESS_TOKEN from the environment. Without --request-token this prints the Kite login URL; after
/// logging in, Kite redirects to your app's redirect URL with `request_token=...` in the query string — pass that here and the command
/// exchanges it (POST /session/token, checksum = SHA256(api_key + request_token + api_secret)) and prints the access token for you to
/// export. The token is deliberately NOT written to disk or to any config/database file: it is a credential for a live brokerage account.
///
/// UNVERIFIED against the real Kite service like the rest of the Zerodha adapter (ZerodhaOptions.AdapterVerifiedAgainstLiveDocs).
/// </summary>
public static class ZerodhaLoginCommand
{
    public static async Task<int> RunAsync(string[] args, ZerodhaOptions zerodha, IServiceProvider provider, ILogger logger, CancellationToken cancellationToken = default)
    {
        var apiKey = Environment.GetEnvironmentVariable(zerodha.ApiKeyEnvVar);
        var apiSecret = Environment.GetEnvironmentVariable(zerodha.ApiSecretEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            Console.Error.WriteLine($"Set {zerodha.ApiKeyEnvVar} and {zerodha.ApiSecretEnvVar} in the environment first (see .env.example).");
            return 1;
        }

        var requestToken = RequestTokenArg(args, out var malformed);
        if (malformed) { Console.Error.WriteLine("--request-token needs a value."); return 1; }

        var transport = new KiteHttpTransport(provider.GetRequiredService<IHttpClientFactory>(), zerodha.RequestTimeoutSeconds);
        var client = new ZerodhaClient(new KiteApiGateway(transport, zerodha, apiKey), apiKey, apiSecret);

        if (requestToken is null)
        {
            Console.WriteLine("1. Open this URL, log in to Kite, and approve the app:");
            Console.WriteLine($"   {client.BuildLoginUrl()}");
            Console.WriteLine("2. Kite redirects to your app's redirect URL with ?request_token=XXXX in the address bar.");
            Console.WriteLine("3. Run:  zerodha-login --request-token XXXX");
            return 0;
        }

        try
        {
            var accessToken = await client.ExchangeRequestTokenAsync(requestToken, cancellationToken);
            Console.WriteLine("Session established. Export the access token for this shell / service (valid until about 06:00 IST tomorrow):");
            Console.WriteLine();
            Console.WriteLine($"  export {zerodha.AccessTokenEnvVar}={accessToken}");
            Console.WriteLine();
            Console.WriteLine("It was NOT saved anywhere. Treat it like a password.");
            logger.LogInformation("zerodha-login: session token obtained (not logged).");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never echo the request token or secret; the exception text from the parser carries only Kite's own error message.
            logger.LogWarning("zerodha-login: token exchange failed: {Message}", ex.Message);
            Console.Error.WriteLine($"Token exchange failed: {ex.Message}");
            Console.Error.WriteLine("A request_token is single-use and expires within minutes — get a fresh one from the login URL and retry.");
            return 1;
        }
    }

    internal static string? RequestTokenArg(string[] args, out bool malformed)
    {
        malformed = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] != "--request-token") continue;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--")) { malformed = true; return null; }
            return args[i + 1].Trim();
        }
        return null;
    }
}
