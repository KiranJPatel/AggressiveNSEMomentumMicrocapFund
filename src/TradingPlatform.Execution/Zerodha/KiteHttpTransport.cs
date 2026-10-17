namespace TradingPlatform.Execution.Zerodha;

/// <summary>Real HTTP implementation of IKiteHttpTransport — mirrors YahooHttpFetcher (Tranche 2) exactly: build request, apply the "X-Kite-Version: 3" header (spec-documented, see KiteChecksumHelper's remarks), send, return status+body untouched for KiteResponseParser to interpret.</summary>
public sealed class KiteHttpTransport : IKiteHttpTransport
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _timeoutSeconds;

    public KiteHttpTransport(IHttpClientFactory httpClientFactory, int timeoutSeconds)
    {
        _httpClientFactory = httpClientFactory;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<KiteHttpResponse> SendAsync(HttpMethod method, string urlWithBase, IReadOnlyDictionary<string, string>? formParams, string authorizationHeader, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, urlWithBase);
        request.Headers.Add("X-Kite-Version", "3");
        if (!string.IsNullOrEmpty(authorizationHeader))
            request.Headers.Add("Authorization", authorizationHeader);

        if (formParams is { Count: > 0 })
            request.Content = new FormUrlEncodedContent(formParams);

        var client = _httpClientFactory.CreateClient(nameof(KiteHttpTransport));
        client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new KiteHttpResponse((int)response.StatusCode, body);
    }
}
