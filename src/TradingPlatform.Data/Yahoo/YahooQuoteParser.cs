using System.Text.Json;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// Parses Yahoo's intraday chart response (range=1d) into a <see cref="RealtimeQuote"/>. Returns null (with a reason) rather than
/// guessing whenever the price or the session range is missing — a quote is either complete and plausible or absent.
/// Field names (meta.regularMarketPrice / regularMarketDayHigh / regularMarketDayLow / regularMarketVolume / regularMarketTime /
/// chartPreviousClose; indicators.quote[0].open) are Yahoo's chart schema as used by the daily parser — UNVERIFIED against a live response.
/// </summary>
public static class YahooQuoteParser
{
    public static (RealtimeQuote? Quote, string? Problem) Parse(string json, string symbol)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var chart = doc.RootElement.GetProperty("chart");
            if (chart.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                return (null, err.TryGetProperty("description", out var d) ? d.GetString() : "Yahoo reported an error.");
            var result = chart.GetProperty("result");
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return (null, "empty result");
            var r = result[0];
            var meta = r.GetProperty("meta");

            decimal? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

            var last = Num(meta, "regularMarketPrice");
            var high = Num(meta, "regularMarketDayHigh");
            var low = Num(meta, "regularMarketDayLow");
            var volume = Num(meta, "regularMarketVolume");
            var prevClose = Num(meta, "chartPreviousClose") ?? Num(meta, "previousClose");
            var time = meta.TryGetProperty("regularMarketTime", out var t) && t.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()) : (DateTimeOffset?)null;

            // Session open = the first non-null open print in today's intraday series.
            decimal? open = null;
            if (r.TryGetProperty("indicators", out var ind) && ind.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.Array && q.GetArrayLength() > 0
                && q[0].TryGetProperty("open", out var opens) && opens.ValueKind == JsonValueKind.Array)
                foreach (var o in opens.EnumerateArray()) if (o.ValueKind == JsonValueKind.Number) { open = o.GetDecimal(); break; }

            if (last is not > 0) return (null, "no regularMarketPrice");
            if (time is null) return (null, "no regularMarketTime (cannot judge freshness)");
            if (high is not > 0 || low is not > 0) return (null, "no day high/low");
            if (prevClose is not > 0) return (null, "no previous close");

            return (new RealtimeQuote(symbol, last.Value, open ?? last.Value, high.Value, low.Value, prevClose.Value, volume ?? 0m, time.Value, "Yahoo"), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, $"unparseable response: {ex.Message}");
        }
    }
}
