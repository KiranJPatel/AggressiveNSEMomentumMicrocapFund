namespace TradingPlatform.Core.Configuration;

/// <summary>
/// "Realtime" config section. While the NSE session is open, paper/live cycles overlay today's forming bar (from a live quote) on the
/// cached daily history so entries, stops and paper fills use current prices instead of yesterday's close. Outside market hours nothing
/// changes: the final daily bars in SQLite are used.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    public bool Enabled { get; set; } = true;

    /// <summary>"Auto" (Kite when API credentials are present, else Yahoo), "Kite", or "Yahoo".</summary>
    public string Provider { get; set; } = "Auto";

    /// <summary>A quote older than this (by the SOURCE's own timestamp) is rejected — this is also what catches a delayed feed.</summary>
    public int MaxQuoteAgeSeconds { get; set; } = 300;

    /// <summary>Upper bound on symbols quoted per cycle (open positions + top scanner candidates); Yahoo is rate-limited.</summary>
    public int MaxSymbolsPerCycle { get; set; } = 60;

    /// <summary>
    /// Partial-session volume is scaled up to a full-day estimate (volume / fraction of session elapsed) only once at least this fraction
    /// has elapsed; before that raw volume is used, which under-states volume and so makes breakout confirmation HARDER (conservative).
    /// </summary>
    public decimal MinSessionFractionForVolumeProjection { get; set; } = 0.15m;

    /// <summary>A price further than this from the previous close is treated as a data error / corporate action and the quote is rejected (NSE circuit limits are 20% at most).</summary>
    public decimal MaxDeviationFromPreviousClosePercent { get; set; } = 25m;

    /// <summary>The quote's previous-close must match the last cached close within this tolerance; a larger gap suggests a split/bonus the cached bars have not seen.</summary>
    public decimal MaxPreviousCloseMismatchPercent { get; set; } = 3m;
}
