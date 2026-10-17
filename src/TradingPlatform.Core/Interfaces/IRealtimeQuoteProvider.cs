using TradingPlatform.Core.Models;

namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// Intraday quote source for paper/live trading (spec §7.3: daily bars alone are a day stale during the session). Implementations
/// return ONLY the symbols they could quote — a missing key means "no quote", never a guessed price — and must not throw for an
/// individual symbol failing.
/// </summary>
public interface IRealtimeQuoteProvider
{
    string SourceName { get; }
    Task<IReadOnlyDictionary<string, RealtimeQuote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken = default);
}
