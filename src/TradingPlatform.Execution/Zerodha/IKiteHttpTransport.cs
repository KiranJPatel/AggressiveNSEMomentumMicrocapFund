namespace TradingPlatform.Execution.Zerodha;

public sealed record KiteHttpResponse(int StatusCode, string Body);

/// <summary>
/// Seam between Kite Connect orchestration logic (auth headers, rate
/// limiting, retry) and the actual network call — mirrors
/// IYahooHttpFetcher's role exactly (Tranche 2). Lets every
/// ZerodhaXxxService be fully unit tested with a fake transport returning
/// canned responses/exceptions, with no dependency on this sandbox's
/// blocked network access to kite.trade.
/// </summary>
public interface IKiteHttpTransport
{
    Task<KiteHttpResponse> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? formParams, string authorizationHeader, CancellationToken cancellationToken = default);
}
