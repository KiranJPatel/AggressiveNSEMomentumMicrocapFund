using TradingPlatform.Core.Enums;

namespace TradingPlatform.Backtesting.Portfolio;

/// <summary>
/// Aggregates BacktestPortfolioState's open positions into the exposure
/// figures PositionSizingCalculator needs as pre-computed inputs (Tranche
/// 8's PositionSizingInputs XML doc: "aggregating the whole portfolio's
/// exposure is a portfolio-level concern the caller owns" — this IS that
/// caller). All exposure values are marked-to-market at CurrentPrices, not
/// at each position's own entry price — a candidate trade's headroom
/// against the sector/microcap caps should reflect what the portfolio is
/// worth today, not what it cost when opened.
///
/// Portfolio heat (spec §4.2's "PortfolioHeat = Σ(OpenPositionRisk /
/// CurrentNAV)") is resolved here as LIVE risk — Quantity × (CurrentPrice
/// − CurrentStop), never the frozen InitialRiskAmount from entry day. This
/// is a deliberate, documented choice (spec doesn't pin down which):
/// using the live, ratcheted CurrentStop means a position the trailing-
/// stop ladder (Tranche 9) has already substantially de-risked correctly
/// contributes less heat today, freeing genuine capacity for new risk —
/// using the stale entry-day figure would overstate heat for exactly the
/// winning positions the whole trailing-stop mechanism exists to protect
/// AND free up capital from.
/// </summary>
public static class PortfolioExposureCalculator
{
    public static decimal ComputeNav(BacktestPortfolioState state, IReadOnlyDictionary<string, decimal> currentPrices)
    {
        var positionsValue = state.OpenPositions.Values.Sum(p => p.Quantity * MarkPrice(p, currentPrices));
        return state.Cash + positionsValue;
    }

    public static decimal ComputePortfolioHeatValue(BacktestPortfolioState state, IReadOnlyDictionary<string, decimal> currentPrices) =>
        state.OpenPositions.Values.Sum(p => Math.Max(0m, p.Quantity * (MarkPrice(p, currentPrices) - p.CurrentStop)));

    public static decimal ComputeSectorExposureValue(BacktestPortfolioState state, IReadOnlyDictionary<string, decimal> currentPrices, IReadOnlyDictionary<string, string?> sectorBySymbol, string? sector)
    {
        if (sector is null) return 0m; // Unknown sector never aggregates against a named sector's cap (spec §2.2)
        return state.OpenPositions.Values
            .Where(p => sectorBySymbol.TryGetValue(p.Symbol, out var s) && s == sector)
            .Sum(p => p.Quantity * MarkPrice(p, currentPrices));
    }

    public static decimal ComputeMicrocapExposureValue(BacktestPortfolioState state, IReadOnlyDictionary<string, decimal> currentPrices, IReadOnlyDictionary<string, MarketCapBucket> bucketBySymbol) =>
        state.OpenPositions.Values
            .Where(p => bucketBySymbol.TryGetValue(p.Symbol, out var b) && b == MarketCapBucket.Micro)
            .Sum(p => p.Quantity * MarkPrice(p, currentPrices));

    /// <summary>Falls back to the position's own average entry price when today's mark is unavailable (e.g. a trading halt) — never silently drops the position from NAV.</summary>
    private static decimal MarkPrice(OpenPositionLot lot, IReadOnlyDictionary<string, decimal> currentPrices) =>
        currentPrices.TryGetValue(lot.Symbol, out var price) ? price : lot.AverageEntryPrice;
}
