using TradingPlatform.Core.Configuration;
using TradingPlatform.Data.Http;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Shared plumbing for every Zerodha*Service — one rate limiter per Kite's
/// two documented tiers (order placement vs everything else, see
/// ZerodhaOptions), one retry policy, one place that knows the
/// Authorization header shape ("token api_key:access_token", spec §6.2 /
/// Kite v3's documented auth header — see KiteChecksumHelper's remarks).
/// Reuses SlidingWindowRateLimiter/ExponentialBackoffRetryPolicy verbatim
/// from Tranche 2's Yahoo work rather than re-implementing either.
/// </summary>
public sealed class KiteApiGateway
{
    private readonly IKiteHttpTransport _transport;
    private readonly ZerodhaOptions _options;
    private readonly string _apiKey;
    private readonly SlidingWindowRateLimiter _orderRateLimiter;
    private readonly SlidingWindowRateLimiter _generalRateLimiter;
    private readonly ExponentialBackoffRetryPolicy _retryPolicy;

    /// <summary>Null until a session is established (via ExchangeRequestTokenAsync or a pre-supplied token from ZerodhaOptions.AccessTokenEnvVar). IsAuthenticated below is a LOCAL, non-network check — it confirms a token is PRESENT, not that Kite still considers it valid (an expired token is only discovered on the next real call, surfacing as KiteTokenException — spec §6.2's "handle... expired token").</summary>
    public string? AccessToken { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);

    public KiteApiGateway(IKiteHttpTransport transport, ZerodhaOptions options, string apiKey, string? initialAccessToken = null)
    {
        _transport = transport;
        _options = options;
        _apiKey = apiKey;
        AccessToken = initialAccessToken;
        _orderRateLimiter = new SlidingWindowRateLimiter(options.OrderRateLimitPerMinute, TimeSpan.FromMinutes(1));
        _generalRateLimiter = new SlidingWindowRateLimiter(options.GeneralRateLimitPerMinute, TimeSpan.FromMinutes(1));
        _retryPolicy = new ExponentialBackoffRetryPolicy(
            options.MaxRetries,
            TimeSpan.FromMilliseconds(options.InitialBackoffMilliseconds),
            TimeSpan.FromMilliseconds(options.MaxBackoffMilliseconds));
    }

    /// <summary>Authenticated call against an order-placement-tier endpoint.</summary>
    public Task<KiteHttpResponse> SendOrderRequestAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, CancellationToken cancellationToken = default)
        => SendAsync(_orderRateLimiter, method, path, formParams, requireAuth: true, cancellationToken);

    /// <summary>Authenticated call against a general-tier endpoint (positions, margins, order status).</summary>
    public Task<KiteHttpResponse> SendGeneralRequestAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, CancellationToken cancellationToken = default)
        => SendAsync(_generalRateLimiter, method, path, formParams, requireAuth: true, cancellationToken);

    /// <summary>Unauthenticated call — only the session-token exchange during login uses this (there is no access token yet).</summary>
    public Task<KiteHttpResponse> SendUnauthenticatedRequestAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, CancellationToken cancellationToken = default)
        => SendAsync(_generalRateLimiter, method, path, formParams, requireAuth: false, cancellationToken);

    private async Task<KiteHttpResponse> SendAsync(SlidingWindowRateLimiter limiter, HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, bool requireAuth, CancellationToken cancellationToken)
    {
        if (requireAuth && !IsAuthenticated)
            throw new KiteTokenException("No access token available — the login flow has not been completed.");

        var url = $"{_options.BaseUrl}{path}";
        var authHeader = requireAuth ? $"token {_apiKey}:{AccessToken}" : "";

        return await _retryPolicy.ExecuteAsync(
            async (_, ct) =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                var response = await _transport.SendAsync(method, url, formParams, authHeader, ct).ConfigureAwait(false);
                KiteResponseParser.ThrowIfError(response.StatusCode, response.Body);
                return response;
            },
            shouldRetry: ex => ex is KiteNetworkException or HttpRequestException or TaskCanceledException,
            cancellationToken).ConfigureAwait(false);
    }
}
