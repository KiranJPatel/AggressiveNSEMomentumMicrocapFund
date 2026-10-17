using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Execution.Zerodha;

namespace TradingPlatform.Application.Cli;

/// <summary>Chooses the quote source for a cycle: Kite when asked for (or "Auto" with API credentials present), otherwise Yahoo. An explicit "Kite" without credentials yields null + a reason — it never silently downgrades.</summary>
public static class RealtimeProviderFactory
{
    public static IRealtimeQuoteProvider? Create(IServiceProvider sp, RealtimeOptions options, ZerodhaOptions zerodha, ILogger logger, out string? problem)
    {
        problem = null;
        var choice = options.Provider.Trim().ToLowerInvariant();
        var apiKey = Environment.GetEnvironmentVariable(zerodha.ApiKeyEnvVar);
        var accessToken = Environment.GetEnvironmentVariable(zerodha.AccessTokenEnvVar);
        var haveKite = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(accessToken);

        if (choice is "kite" or "auto" && haveKite)
        {
            var transport = new KiteHttpTransport(sp.GetRequiredService<IHttpClientFactory>(), zerodha.RequestTimeoutSeconds);
            return new KiteRealtimeQuoteProvider(new KiteApiGateway(transport, zerodha, apiKey!, accessToken!), logger);
        }
        if (choice == "kite") { problem = $"Realtime:Provider=Kite but {zerodha.ApiKeyEnvVar}/{zerodha.AccessTokenEnvVar} are not both set."; return null; }
        if (choice is "yahoo" or "auto")
        {
            var intraday = sp.GetService<IYahooIntradayFetcher>();
            if (intraday is null) { problem = "no Yahoo intraday fetcher is registered."; return null; }
            return new YahooRealtimeQuoteProvider(intraday, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<YahooFinanceOptions>>().Value, logger);
        }
        problem = $"Realtime:Provider '{options.Provider}' must be Auto, Kite or Yahoo.";
        return null;
    }
}
