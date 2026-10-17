namespace TradingPlatform.Core.Models;

/// <summary>
/// A snapshot of today's session for one symbol from a real-time source. <see cref="Volume"/> is CUMULATIVE shares traded so far today;
/// <see cref="QuoteTimeUtc"/> is when the SOURCE says the quote was produced (so a delayed feed is detectable as an old timestamp).
/// </summary>
public sealed record RealtimeQuote(
    string Symbol,
    decimal LastPrice,
    decimal Open,
    decimal DayHigh,
    decimal DayLow,
    decimal PreviousClose,
    decimal Volume,
    DateTimeOffset QuoteTimeUtc,
    string Source);
