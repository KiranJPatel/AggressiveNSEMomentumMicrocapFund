using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// IRealtimeQuoteProvider over Kite Connect GET /quote (official exchange feed; needs the same API key + access token as live trading).
/// Batches symbols (Kite allows 500 per call; 200 used) and returns only what Kite quoted. Any HTTP/parse failure for a batch drops that
/// batch's quotes (logged) — the caller then fails closed for those symbols.
/// </summary>
public sealed class KiteRealtimeQuoteProvider : IRealtimeQuoteProvider
{
    private const int BatchSize = 200;
    private readonly KiteApiGateway _gateway;
    private readonly ILogger _logger;

    public KiteRealtimeQuoteProvider(KiteApiGateway gateway, ILogger logger) { _gateway = gateway; _logger = logger; }
    public string SourceName => "Kite";

    public async Task<IReadOnlyDictionary<string, RealtimeQuote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, RealtimeQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in symbols.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
        {
            var path = "/quote?" + string.Join("&", batch.Select(s => "i=" + Uri.EscapeDataString("NSE:" + s)));
            try
            {
                var response = await _gateway.SendGeneralRequestAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                foreach (var q in KiteQuoteParser.Parse(response.StatusCode, response.Body)) result[q.Symbol] = q;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning("realtime: Kite /quote batch of {Count} failed: {Message}", batch.Length, ex.Message); }
        }
        return result;
    }
}
