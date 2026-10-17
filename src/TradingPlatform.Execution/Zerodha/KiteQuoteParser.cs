using System.Globalization;
using System.Text.Json;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Parses Kite Connect's GET /quote response: data["NSE:SYMBOL"] = { last_price, volume, timestamp, ohlc{open,high,low,close}, … } where
/// ohlc.close is the PREVIOUS session's close. Timestamps are IST wall-clock "yyyy-MM-dd HH:mm:ss". Field names follow Kite's published
/// API docs — UNVERIFIED against a live account (see ZerodhaOptions.AdapterVerifiedAgainstLiveDocs). An entry missing any required
/// field is omitted, never defaulted.
/// </summary>
public static class KiteQuoteParser
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static IReadOnlyList<RealtimeQuote> Parse(int statusCode, string body)
    {
        if (statusCode < 200 || statusCode >= 300) throw new KiteDataException($"Kite /quote returned HTTP {statusCode}.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException ex) { throw new KiteDataException($"Kite /quote returned malformed JSON: {ex.Message}"); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return Array.Empty<RealtimeQuote>();
            var list = new List<RealtimeQuote>();
            foreach (var entry in data.EnumerateObject())
            {
                var v = entry.Value;
                if (v.ValueKind != JsonValueKind.Object) continue;
                var symbol = entry.Name.Contains(':') ? entry.Name[(entry.Name.IndexOf(':') + 1)..] : entry.Name;
                if (!Num(v, "last_price", out var last) || last <= 0) continue;
                if (!v.TryGetProperty("ohlc", out var ohlc) || ohlc.ValueKind != JsonValueKind.Object) continue;
                if (!Num(ohlc, "open", out var open) || !Num(ohlc, "high", out var high) || !Num(ohlc, "low", out var low) || !Num(ohlc, "close", out var prev)) continue;
                if (high <= 0 || low <= 0 || prev <= 0) continue;
                Num(v, "volume", out var volume);
                var tsText = v.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
                if (tsText is null || !DateTime.TryParseExact(tsText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue; // no timestamp => freshness unknowable => no quote
                list.Add(new RealtimeQuote(symbol, last, open > 0 ? open : last, high, low, prev, volume, new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Ist).ToUniversalTime(), "Kite"));
            }
            return list;
        }
    }

    private static bool Num(JsonElement e, string name, out decimal value)
    {
        value = 0m;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return false;
        value = p.GetDecimal(); return true;
    }
}
