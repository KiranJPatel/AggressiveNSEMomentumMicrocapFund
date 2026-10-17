using TradingPlatform.Core.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Data.Http;

namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// Real implementation of IYahooHttpFetcher: builds the chart URL, applies
/// the hand-rolled rate limiter + retry/backoff (Polly unavailable — see
/// docs/gap-log.md), and returns the raw JSON body. Uses IHttpClientFactory,
/// which ships in the Microsoft.AspNetCore.App shared framework and needs
/// no NuGet restore (see FrameworkReference in this project's .csproj).
/// </summary>
public sealed class YahooHttpFetcher : IYahooHttpFetcher, IYahooIntradayFetcher, IYahooIntervalFetcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YahooFinanceOptions _options;
    private readonly SlidingWindowRateLimiter _rateLimiter;
    private readonly ExponentialBackoffRetryPolicy _retryPolicy;

    public YahooHttpFetcher(IHttpClientFactory httpClientFactory, YahooFinanceOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _rateLimiter = new SlidingWindowRateLimiter(options.RateLimitRequestsPerMinute, TimeSpan.FromMinutes(1));
        _retryPolicy = new ExponentialBackoffRetryPolicy(
            options.MaxRetries,
            TimeSpan.FromMilliseconds(options.InitialBackoffMilliseconds),
            TimeSpan.FromMilliseconds(options.MaxBackoffMilliseconds));
    }

    public async Task<string> FetchIntradayRawJsonAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var path = string.Format(_options.ChartPathTemplate, Uri.EscapeDataString(symbol));
        // range=1d&interval=5m: today's session only; meta carries the last price / day high-low / volume / quote time, and the 5-minute
        // arrays give the session's real opening print. (Field names are Yahoo's documented-by-use chart schema — UNVERIFIED here.)
        var url = $"{_options.BaseUrl}{path}?range=1d&interval=5m&includePrePost=false";
        return await FetchAsync(symbol, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> FetchRawJsonAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), IndiaStandardTime.Offset).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), IndiaStandardTime.Offset).ToUnixTimeSeconds();

        var path = string.Format(_options.ChartPathTemplate, Uri.EscapeDataString(symbol));
        var url = $"{_options.BaseUrl}{path}?period1={period1}&period2={period2}&interval=1d&events=div%2Csplits";

        return await FetchAsync(symbol, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> FetchIntervalRawJsonAsync(string symbol, DateOnly from, DateOnly to, string interval, CancellationToken cancellationToken = default)
    {
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), IndiaStandardTime.Offset).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), IndiaStandardTime.Offset).ToUnixTimeSeconds();
        var path = string.Format(_options.ChartPathTemplate, Uri.EscapeDataString(symbol));
        // Regular session only. UNVERIFIED against the live service like the rest of the adapter: interval tokens and the retention
        // limits (YahooIntradayLimits) are Yahoo's documented-by-use behaviour.
        var url = $"{_options.BaseUrl}{path}?period1={period1}&period2={period2}&interval={Uri.EscapeDataString(interval)}&includePrePost=false";
        return await FetchAsync(symbol, url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> FetchAsync(string symbol, string url, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(
            async (_, ct) =>
            {
                await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);

                var client = _httpClientFactory.CreateClient(nameof(YahooHttpFetcher));
                client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw new YahooHttpException(symbol, (int)response.StatusCode, body);

                return body;
            },
            shouldRetry: ex => ex is YahooHttpException { StatusCode: 429 or >= 500 } or HttpRequestException or TaskCanceledException,
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class YahooHttpException : Exception
{
    public string Symbol { get; }
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public YahooHttpException(string symbol, int statusCode, string responseBody)
        : base($"Yahoo request for {symbol} failed with HTTP {statusCode}.")
    {
        Symbol = symbol;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
