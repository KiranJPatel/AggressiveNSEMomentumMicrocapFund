namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// Low-level Kite Connect HTTP contract (spec §6.2). UNVERIFIED against live
/// docs until confirmed with network access — see ZerodhaOptions.AdapterVerifiedAgainstLiveDocs
/// and docs/gap-log.md. Implemented directly against the documented REST
/// contract if the official SDK NuGet package cannot be restored (spec §1.3).
/// </summary>
public interface IZerodhaClient
{
    Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default);

    /// <summary>URL to send the user to for the Kite-hosted login step of the OAuth-like flow (spec §6.2).</summary>
    string BuildLoginUrl();

    /// <summary>Exchanges the request_token Kite redirects back with (after the user logs in at BuildLoginUrl's URL) for an access_token, and stores it for subsequent authenticated calls. Returns the access_token.</summary>
    Task<string> ExchangeRequestTokenAsync(string requestToken, CancellationToken cancellationToken = default);
}
