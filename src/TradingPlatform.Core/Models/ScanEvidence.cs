namespace TradingPlatform.Core.Models;

/// <summary>The computed inputs behind one ranked scanner row — its 100-point score breakdown, VCP pattern (if any) and last indicator values — kept so a ranking can be audited later (StockScores / VcpPatterns / Indicators tables, spec §7.2).</summary>
public sealed record ScanEvidence(string Symbol, StockScore Score, VcpPattern? Vcp, IndicatorSnapshot Indicators, PriceStructure? Structure = null);
