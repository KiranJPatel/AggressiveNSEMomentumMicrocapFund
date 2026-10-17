using TradingPlatform.Execution.Zerodha;

namespace TradingPlatform.Tests.ExecutionTests;

/// <summary>Test double for IKiteHttpTransport — lets the Zerodha*Service classes and KiteApiGateway be tested end-to-end (request building, auth headers, rate limiting, retry, response parsing) with no real network access, which this sandbox doesn't have to api.kite.trade anyway (see docs/gap-log.md).</summary>
public sealed class FakeKiteHttpTransport : IKiteHttpTransport
{
    private readonly Func<HttpMethod, string, IReadOnlyDictionary<string, string>?, string, KiteHttpResponse> _responder;
    public List<(HttpMethod Method, string Path, IReadOnlyDictionary<string, string>? FormParams, string AuthorizationHeader)> Calls { get; } = new();

    public FakeKiteHttpTransport(Func<HttpMethod, string, IReadOnlyDictionary<string, string>?, string, KiteHttpResponse> responder) => _responder = responder;

    public Task<KiteHttpResponse> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, string authorizationHeader, CancellationToken cancellationToken = default)
    {
        Calls.Add((method, path, formParams, authorizationHeader));
        return Task.FromResult(_responder(method, path, formParams, authorizationHeader));
    }
}
