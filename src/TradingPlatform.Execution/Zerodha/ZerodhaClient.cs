using TradingPlatform.Core.Interfaces;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Implements IZerodhaClient (spec §6.2). Owns the login/session-exchange
/// flow: BuildLoginUrl sends the user to Kite's hosted login page;
/// ExchangeRequestTokenAsync trades the resulting request_token for an
/// access_token via POST /session/token, using KiteChecksumHelper's
/// documented checksum construction. UNVERIFIED — see
/// KiteChecksumHelper's remarks.
/// </summary>
public sealed class ZerodhaClient : IZerodhaClient
{
    private readonly KiteApiGateway _gateway;
    private readonly string _apiKey;
    private readonly string _apiSecret;

    public ZerodhaClient(KiteApiGateway gateway, string apiKey, string apiSecret)
    {
        _gateway = gateway;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
    }

    public string BuildLoginUrl() => KiteChecksumHelper.BuildLoginUrl(_apiKey);

    public async Task<string> ExchangeRequestTokenAsync(string requestToken, CancellationToken cancellationToken = default)
    {
        var checksum = KiteChecksumHelper.ComputeChecksum(_apiKey, requestToken, _apiSecret);
        var formParams = new Dictionary<string, string>
        {
            ["api_key"] = _apiKey,
            ["request_token"] = requestToken,
            ["checksum"] = checksum,
        };

        var response = await _gateway.SendUnauthenticatedRequestAsync(HttpMethod.Post, "/session/token", formParams, cancellationToken).ConfigureAwait(false);
        var accessToken = KiteResponseParser.ParseSessionToken(response.StatusCode, response.Body);

        _gateway.AccessToken = accessToken;
        return accessToken;
    }

    /// <summary>
    /// A LOCAL check only — confirms an access token is present, not that
    /// Kite still considers it valid server-side (spec §6.2's "handle...
    /// expired token" is instead handled by every authenticated call
    /// surfacing a KiteTokenException the moment an expired token is
    /// actually used — there is no documented lightweight "is my token
    /// still valid" endpoint to poll cheaply, and doing a real round trip
    /// just to answer this question would cost a rate-limit slot for no
    /// benefit over just trying the real call and handling the failure).
    /// </summary>
    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(_gateway.IsAuthenticated);
}
