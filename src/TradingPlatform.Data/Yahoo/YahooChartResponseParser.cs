using System.Text.Json;
using TradingPlatform.Core.Abstractions;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Data.Yahoo;

public sealed record YahooParseResult(IReadOnlyList<HistoricalBar> Bars, IReadOnlyList<string> Warnings);

/// <summary>
/// Pure JSON-parsing logic for a Yahoo chart response — no I/O, fully unit
/// testable against hand-built fixture payloads (see
/// docs/gap-log.md: this environment cannot reach Yahoo to capture a real
/// response, so fixtures are built from documented/known JSON shape).
///
/// Timestamp normalization: for Daily bars, Yahoo's per-candle Unix epoch
/// second doesn't reliably tell us whether it represents UTC midnight,
/// exchange-local midnight, or session-open — rather than guess, every
/// timestamp is converted to IST (a fixed, DST-free offset — see
/// IndiaStandardTime) and collapsed to that calendar date's IST midnight.
/// This makes the (Symbol, Timestamp, Timeframe) unique key deterministic
/// regardless of Yahoo's exact epoch convention.
/// </summary>
public static class YahooChartResponseParser
{
    public static YahooParseResult Parse(string json, string symbol, string source, Timeframe timeframe)
    {
        var warnings = new List<string>();

        YahooChartResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<YahooChartResponse>(json);
        }
        catch (JsonException ex)
        {
            throw new YahooResponseException($"Malformed JSON from Yahoo for {symbol}: {ex.Message}");
        }

        if (response?.Chart is null)
            throw new YahooResponseException($"Empty/unrecognized chart response for {symbol}.");

        if (response.Chart.Error is { } error)
            throw new YahooResponseException($"Yahoo returned an error for {symbol}: {error.Code} - {error.Description}");

        var result = response.Chart.Result?.FirstOrDefault();
        if (result is null)
        {
            warnings.Add($"No result entries for {symbol} — likely no data in the requested range.");
            return new YahooParseResult(Array.Empty<HistoricalBar>(), warnings);
        }

        var timestamps = result.Timestamp ?? new List<long>();
        var quote = result.Indicators?.Quote?.FirstOrDefault();
        var adjClose = result.Indicators?.AdjClose?.FirstOrDefault()?.AdjClose;

        if (quote is null)
        {
            warnings.Add($"No 'quote' indicators block for {symbol}.");
            return new YahooParseResult(Array.Empty<HistoricalBar>(), warnings);
        }

        var bars = new List<HistoricalBar>(timestamps.Count);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < timestamps.Count; i++)
        {
            var open = GetAt(quote.Open, i);
            var high = GetAt(quote.High, i);
            var low = GetAt(quote.Low, i);
            var close = GetAt(quote.Close, i);
            var volume = GetAt(quote.Volume, i);
            var adj = adjClose is not null ? GetAt(adjClose, i) : close;

            if (open is null || high is null || low is null || close is null || volume is null)
            {
                warnings.Add($"{symbol}: skipped candle at index {i} (epoch {(i < timestamps.Count ? timestamps[i] : -1)}) — one or more OHLCV fields were null (Yahoo does this for halts/missing sessions).");
                continue;
            }

            var epochSeconds = timestamps[i];
            var utc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            var istInstant = utc.ToOffset(IndiaStandardTime.Offset);
            var tradingDate = DateOnly.FromDateTime(istInstant.DateTime);
            // Daily/Weekly: collapse to the IST date's midnight (deterministic key). Intraday: the candle's own IST start time IS the key —
            // collapsing it to midnight would merge every bar of a session into one.
            var normalizedTimestamp = timeframe is Timeframe.H1 or Timeframe.M15 or Timeframe.M5
                ? new DateTimeOffset(istInstant.Year, istInstant.Month, istInstant.Day, istInstant.Hour, istInstant.Minute, 0, IndiaStandardTime.Offset)
                : new DateTimeOffset(tradingDate.ToDateTime(TimeOnly.MinValue), IndiaStandardTime.Offset);

            bars.Add(new HistoricalBar
            {
                Symbol = symbol,
                Timestamp = normalizedTimestamp,
                Open = (decimal)open.Value,
                High = (decimal)high.Value,
                Low = (decimal)low.Value,
                Close = (decimal)close.Value,
                AdjustedClose = adj.HasValue ? (decimal)adj.Value : (decimal)close.Value,
                Volume = volume.Value,
                Source = source,
                Timeframe = timeframe,
                CreatedAt = now
            });
        }

        return new YahooParseResult(bars, warnings);
    }

    private static T? GetAt<T>(IReadOnlyList<T?>? list, int index) where T : struct =>
        list is not null && index < list.Count ? list[index] : null;
}

public sealed class YahooResponseException : Exception
{
    public YahooResponseException(string message) : base(message) { }
}
