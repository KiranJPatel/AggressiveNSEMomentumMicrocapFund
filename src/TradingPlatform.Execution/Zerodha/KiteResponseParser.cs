using System.Text.Json;
using System.Text.Json.Serialization;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Pure JSON-parsing logic for Kite Connect v3 responses — no I/O, unit
/// tested against hand-built fixture payloads matching the documented
/// shape (this sandbox cannot reach kite.trade to capture real responses
/// — same methodology as YahooChartResponseParser, Tranche 2). UNVERIFIED
/// — see KiteChecksumHelper's remarks; margins/positions response shapes
/// in particular are reconstructed from documentation and common client
/// usage patterns rather than a captured live response, and should be the
/// first thing double-checked in the required manual doc-diff pass.
/// </summary>
public static class KiteResponseParser
{
    public static string ParseOrderPlacement(int httpStatusCode, string json)
    {
        var envelope = DeserializeOrThrow(json);
        if (envelope.Status != "success")
            throw MapError(httpStatusCode, envelope);

        var orderId = envelope.Data.GetProperty("order_id").GetString();
        return orderId ?? throw new KiteDataException("Order placement succeeded but response carried no order_id.");
    }

    public static string ParseSessionToken(int httpStatusCode, string json)
    {
        var envelope = DeserializeOrThrow(json);
        if (envelope.Status != "success")
            throw MapError(httpStatusCode, envelope);

        var accessToken = envelope.Data.GetProperty("access_token").GetString();
        return accessToken ?? throw new KiteDataException("Session exchange succeeded but response carried no access_token.");
    }

    /// <summary>Parses GET /portfolio/positions' "net" array (net, EOD-carried-forward positions — the figure relevant for delivery-equity reconciliation, spec §6.4) into Core Position records. Only Quantity/AverageEntryPrice/Symbol are populated — fields with no broker equivalent (InitialStop, RiskPerShare, etc.) are left at their type default, since they are local-only planning state the broker has no concept of.</summary>
    public static IReadOnlyList<Position> ParsePositions(int httpStatusCode, string json)
    {
        var envelope = DeserializeOrThrow(json);
        if (envelope.Status != "success")
            throw MapError(httpStatusCode, envelope);

        if (!envelope.Data.TryGetProperty("net", out var netArray) || netArray.ValueKind != JsonValueKind.Array)
            throw new KiteDataException("Positions response missing expected 'net' array.");

        var positions = new List<Position>();
        foreach (var item in netArray.EnumerateArray())
        {
            var quantity = item.GetProperty("quantity").GetDecimal();
            if (quantity == 0) continue; // a fully squared-off symbol still appears with qty 0 — not an open position

            positions.Add(new Position
            {
                Symbol = item.GetProperty("tradingsymbol").GetString() ?? throw new KiteDataException("Position entry missing tradingsymbol."),
                Quantity = quantity,
                AverageEntryPrice = item.TryGetProperty("average_price", out var avgPrice) ? avgPrice.GetDecimal() : 0m,
                IsOpen = true,
            });
        }
        return positions;
    }

    /// <summary>Parses GET /user/margins (or /user/margins/equity) for the equity segment's available cash.</summary>
    public static decimal ParseAvailableCash(int httpStatusCode, string json)
    {
        var envelope = DeserializeOrThrow(json);
        if (envelope.Status != "success")
            throw MapError(httpStatusCode, envelope);

        var equity = envelope.Data.TryGetProperty("equity", out var eq) ? eq : envelope.Data; // /user/margins/equity returns the segment object directly, not nested
        if (!equity.TryGetProperty("available", out var available) || !available.TryGetProperty("cash", out var cash))
            throw new KiteDataException("Margins response missing expected equity.available.cash.");

        return cash.GetDecimal();
    }

    /// <summary>Parses GET /orders' array of order objects into Order records. Kite reports its own order_id and status string per order; State is mapped via MapOrderStatus below. Local-only fields (Id, SignalId, CorrelationId, timestamps beyond what Kite reports) are left unset — the caller (Order Manager) is responsible for matching these back to its own local Order rows via BrokerOrderId.</summary>
    public static IReadOnlyList<Order> ParseOrders(int httpStatusCode, string json)
    {
        var envelope = DeserializeOrThrow(json);
        if (envelope.Status != "success")
            throw MapError(httpStatusCode, envelope);

        if (envelope.Data.ValueKind != JsonValueKind.Array)
            throw new KiteDataException("Orders response 'data' was not an array.");

        var orders = new List<Order>();
        foreach (var item in envelope.Data.EnumerateArray())
        {
            orders.Add(new Order
            {
                Symbol = item.GetProperty("tradingsymbol").GetString() ?? throw new KiteDataException("Order entry missing tradingsymbol."),
                Side = item.GetProperty("transaction_type").GetString() == "BUY" ? OrderSide.Buy : OrderSide.Sell,
                Type = item.TryGetProperty("order_type", out var ot) && ot.GetString() == "LIMIT" ? OrderType.Limit : OrderType.Market,
                Quantity = item.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m,
                BrokerOrderId = item.TryGetProperty("order_id", out var oid) ? oid.GetString() : null,
                // UNVERIFIED against live Kite (AdapterVerifiedAgainstLiveDocs=false): documented field names filled_quantity / average_price.
                FilledQuantity = item.TryGetProperty("filled_quantity", out var fq) && fq.ValueKind == JsonValueKind.Number ? fq.GetDecimal() : 0m,
                AverageFillPrice = item.TryGetProperty("average_price", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetDecimal() > 0m ? ap.GetDecimal() : null,
                State = MapOrderStatus(item.TryGetProperty("status", out var st) ? st.GetString() : null),
                CorrelationId = item.TryGetProperty("tag", out var tag) ? tag.GetString() ?? "" : "",
            });
        }
        return orders;
    }

    /// <summary>Kite's documented order status strings, mapped to our OrderState enum (spec §6.3). Anything not recognized maps to Unknown — never guessed into a more specific state.</summary>
    private static OrderState MapOrderStatus(string? kiteStatus) => kiteStatus switch
    {
        "PUT ORDER REQ RECEIVED" or "VALIDATION PENDING" => OrderState.Validated,
        "OPEN PENDING" or "MODIFY PENDING" or "MODIFY VALIDATION PENDING" => OrderState.Acknowledged,
        "OPEN" or "TRIGGER PENDING" => OrderState.Open,
        "COMPLETE" => OrderState.Filled,
        "CANCELLED" or "CANCELLED AMO" => OrderState.Cancelled,
        "REJECTED" => OrderState.Rejected,
        _ => OrderState.Unknown,
    };

    public static void ThrowIfError(int httpStatusCode, string json)
    {
        if (httpStatusCode is >= 200 and < 300) return;

        // A non-2xx response might still be a well-formed Kite error envelope, or might be
        // a raw proxy/gateway error page (e.g. a 502 from an intermediary) — try to parse it
        // as the documented envelope first, and fall back to a generic exception carrying the
        // raw body if that fails, rather than losing the failure to a swallowed JsonException.
        KiteResponseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<KiteResponseEnvelope>(json);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            throw httpStatusCode switch
            {
                429 => new KiteNetworkException($"Rate limited (HTTP 429): {Truncate(json)}", 429, "RateLimited"),
                >= 500 => new KiteNetworkException($"HTTP {httpStatusCode}: {Truncate(json)}", httpStatusCode, "Unparseable"),
                _ => new KiteGeneralException($"HTTP {httpStatusCode}: {Truncate(json)}", httpStatusCode, "Unparseable"),
            };
        }

        throw MapError(httpStatusCode, envelope);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    private static KiteResponseEnvelope DeserializeOrThrow(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<KiteResponseEnvelope>(json);
            return envelope ?? throw new KiteDataException("Empty/null Kite response body.");
        }
        catch (JsonException ex)
        {
            throw new KiteDataException($"Malformed JSON from Kite: {ex.Message}");
        }
    }

    private static KiteException MapError(int httpStatusCode, KiteResponseEnvelope envelope)
    {
        var message = envelope.Message ?? "Kite returned an error with no message.";
        return envelope.ErrorType switch
        {
            "TokenException" => new KiteTokenException(message, envelope.ErrorType),
            "PermissionException" => new KitePermissionException(message, envelope.ErrorType),
            "OrderException" => new KiteOrderException(message, envelope.ErrorType),
            "InputException" => new KiteInputException(message, envelope.ErrorType),
            "NetworkException" => new KiteNetworkException(message, httpStatusCode, envelope.ErrorType),
            "DataException" => new KiteDataException(message, envelope.ErrorType),
            _ when httpStatusCode == 429 => new KiteNetworkException($"Rate limited: {message}", 429, "RateLimited"),
            _ when httpStatusCode == 403 => new KiteTokenException(message, envelope.ErrorType),
            _ => new KiteGeneralException(message, httpStatusCode, envelope.ErrorType),
        };
    }

    private sealed class KiteResponseEnvelope
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public JsonElement Data { get; set; }
        [JsonPropertyName("error_type")] public string? ErrorType { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
