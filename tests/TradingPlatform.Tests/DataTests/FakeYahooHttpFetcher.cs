using TradingPlatform.Data.Yahoo;

namespace TradingPlatform.Tests.DataTests;

/// <summary>
/// Test double for IYahooHttpFetcher — lets YahooFinanceHistoricalDataProvider
/// be tested end-to-end (cache check, missing-range calc, validation,
/// persistence) without any real network access, which this sandbox doesn't
/// have to query1.finance.yahoo.com anyway (see docs/gap-log.md).
/// </summary>
public sealed class FakeYahooHttpFetcher : IYahooHttpFetcher
{
    private readonly Func<string, DateOnly, DateOnly, string> _responder;
    public List<(string Symbol, DateOnly From, DateOnly To)> Calls { get; } = new();

    public FakeYahooHttpFetcher(Func<string, DateOnly, DateOnly, string> responder) => _responder = responder;

    public Task<string> FetchRawJsonAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        Calls.Add((symbol, from, to));
        return Task.FromResult(_responder(symbol, from, to));
    }
}
