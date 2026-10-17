namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Yahoo-fetchable symbols for the broad-market/midcap benchmark indices
/// the strategy needs throughout (regime engine's Nifty-trend signal §3.1,
/// relative-strength engine's index blend §3.6, backtest/scan CLI wiring
/// §7.3 — Tranche 18). Spec never names a symbol convention, so this is a
/// resolved ambiguity (§0.4): BroadIndexSymbol defaults to "^CRSLDX",
/// confirmed via web search as Nifty 500's live Yahoo Finance ticker.
/// MidcapIndexSymbol defaults to "NMIDCAP150.NS" (NSE's own ticker for the
/// Nifty Midcap 150 index, ".NS"-suffixed per this codebase's existing
/// Yahoo convention) but is explicitly UNVERIFIED — unlike ^CRSLDX, no
/// search result in this sandbox confirmed Yahoo actually carries midcap
/// index data under that symbol (Yahoo's India index coverage is
/// inconsistent beyond NIFTY 50/BANK/500). Treat it the same way the
/// Zerodha adapter (Tranche 12) was flagged: verify against the real Yahoo
/// endpoint once network access exists, before relying on it in
/// production. If it 404s, MidcapIndexSymbol can be set to null in config —
/// every engine that consumes MidcapIndexBars already treats it as
/// optional and renormalizes rather than fabricating a value (RS engine,
/// Tranche 5).
/// </summary>
public sealed class BenchmarkOptions
{
    public const string SectionName = "Benchmark";

    public string BroadIndexSymbol { get; set; } = "^CRSLDX";
    public string? MidcapIndexSymbol { get; set; } = "NMIDCAP150.NS";
}
