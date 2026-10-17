namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// Seam between YahooFinanceHistoricalDataProvider's orchestration logic
/// (cache check, missing-range calc, validation, persistence) and the actual
/// network call. Lets tests exercise the full provider with a fake fetcher
/// returning canned JSON/exceptions — no real sockets, and no dependency on
/// this sandbox's blocked network access to query1.finance.yahoo.com.
/// </summary>
/// <summary>Intraday (today's session) chart for one symbol. A SEPARATE interface so existing IYahooHttpFetcher fakes are unaffected; YahooHttpFetcher implements both and they share one rate limiter.</summary>
public interface IYahooIntradayFetcher
{
    Task<string> FetchIntradayRawJsonAsync(string symbol, CancellationToken cancellationToken = default);
}

/// <summary>Chart download at an intraday interval ("60m", "15m", "5m") over a date range. A SEPARATE interface so existing IYahooHttpFetcher fakes are unaffected.</summary>
public interface IYahooIntervalFetcher
{
    Task<string> FetchIntervalRawJsonAsync(string symbol, DateOnly from, DateOnly to, string interval, CancellationToken cancellationToken = default);
}

public interface IYahooHttpFetcher
{
    Task<string> FetchRawJsonAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
