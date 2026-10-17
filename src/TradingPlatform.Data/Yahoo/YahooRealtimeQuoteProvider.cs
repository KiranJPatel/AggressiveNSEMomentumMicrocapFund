using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// IRealtimeQuoteProvider over Yahoo's chart endpoint (no API key). One request per symbol, paced by the shared fetcher's rate limiter
/// (YahooFinance:RateLimitRequestsPerMinute), a few in flight at a time. A symbol that fails is simply absent from the result.
/// Yahoo's NSE feed may be delayed; that is NOT hidden — the quote's own timestamp is returned and the caller rejects stale quotes.
/// </summary>
public sealed class YahooRealtimeQuoteProvider : IRealtimeQuoteProvider
{
    private const int Parallelism = 4;
    private readonly IYahooIntradayFetcher _fetcher;
    private readonly YahooFinanceOptions _options;
    private readonly ILogger _logger;

    public YahooRealtimeQuoteProvider(IYahooIntradayFetcher fetcher, YahooFinanceOptions options, ILogger logger)
    { _fetcher = fetcher; _options = options; _logger = logger; }

    public string SourceName => "Yahoo";

    public async Task<IReadOnlyDictionary<string, RealtimeQuote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken = default)
    {
        var result = new ConcurrentDictionary<string, RealtimeQuote>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(symbols.Distinct(StringComparer.OrdinalIgnoreCase), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (symbol, ct) =>
        {
            var yahooSymbol = symbol.StartsWith('^') || symbol.EndsWith(_options.DefaultSymbolSuffix, StringComparison.OrdinalIgnoreCase) ? symbol : symbol + _options.DefaultSymbolSuffix;
            try
            {
                var json = await _fetcher.FetchIntradayRawJsonAsync(yahooSymbol, ct).ConfigureAwait(false);
                var (quote, problem) = YahooQuoteParser.Parse(json, symbol);
                if (quote is null) _logger.LogWarning("realtime: no usable Yahoo quote for {Symbol}: {Problem}", symbol, problem);
                else result[symbol] = quote;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("realtime: Yahoo quote for {Symbol} failed: {Message}", symbol, ex.Message); }
        }).ConfigureAwait(false);
        return result;
    }
}
