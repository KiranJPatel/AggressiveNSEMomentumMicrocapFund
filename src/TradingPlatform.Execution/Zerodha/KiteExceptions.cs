namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Base class for Kite Connect API errors. UNVERIFIED (see
/// KiteChecksumHelper's remarks) — exception names/default codes mirror
/// the documented kiteconnect.exceptions hierarchy (TokenException 403,
/// PermissionException 403, OrderException 500, InputException 400,
/// NetworkException 503, DataException 502, GeneralException 500),
/// mapped from the response's "error_type" field (or the HTTP status
/// alone if the body isn't valid JSON) — spec §6.2 requires handling
/// "auth failure, expired token, rate limiting, order rejection, invalid
/// instrument, insufficient funds, market closed, partial fill, network
/// failure, unknown order state" gracefully, which starts with being able
/// to tell these apart programmatically rather than catching one generic
/// HTTP exception.
/// </summary>
public abstract class KiteException : Exception
{
    public int HttpStatusCode { get; }
    public string? ErrorType { get; }

    protected KiteException(string message, int httpStatusCode, string? errorType) : base(message)
    {
        HttpStatusCode = httpStatusCode;
        ErrorType = errorType;
    }
}

/// <summary>Auth/session errors — expired or invalid access token (403). The caller must re-run the login flow; never retried automatically.</summary>
public sealed class KiteTokenException : KiteException
{
    public KiteTokenException(string message, string? errorType = "TokenException") : base(message, 403, errorType) { }
}

/// <summary>The authenticated user lacks permission for the requested action (403).</summary>
public sealed class KitePermissionException : KiteException
{
    public KitePermissionException(string message, string? errorType = "PermissionException") : base(message, 403, errorType) { }
}

/// <summary>Order-specific rejection from the broker's OMS — insufficient funds, invalid instrument, market closed, etc. (500).</summary>
public sealed class KiteOrderException : KiteException
{
    public KiteOrderException(string message, string? errorType = "OrderException") : base(message, 500, errorType) { }
}

/// <summary>Malformed/invalid request parameters (400) — a bug in the caller's request construction, never retried.</summary>
public sealed class KiteInputException : KiteException
{
    public KiteInputException(string message, string? errorType = "InputException") : base(message, 400, errorType) { }
}

/// <summary>Network issue between Kite and its backend OMS (503) — includes rate limiting (429 is mapped here too, since both call for the same backoff-and-retry response).</summary>
public sealed class KiteNetworkException : KiteException
{
    public KiteNetworkException(string message, int httpStatusCode = 503, string? errorType = "NetworkException") : base(message, httpStatusCode, errorType) { }
}

/// <summary>Bad/unexpected response shape from the backend OMS (502).</summary>
public sealed class KiteDataException : KiteException
{
    public KiteDataException(string message, string? errorType = "DataException") : base(message, 502, errorType) { }
}

/// <summary>Catch-all for anything not covered by a more specific type above.</summary>
public sealed class KiteGeneralException : KiteException
{
    public KiteGeneralException(string message, int httpStatusCode, string? errorType) : base(message, httpStatusCode, errorType) { }
}
