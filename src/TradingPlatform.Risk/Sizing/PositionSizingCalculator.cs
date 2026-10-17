using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Risk.Sizing;

/// <summary>
/// Implements spec §4.1's position sizing formula and constraint pipeline
/// literally:
///
///   RiskAmount   = NAV x RiskPercent
///   RiskPerShare = EntryPrice - StopPrice
///   PositionQty  = RiskAmount / RiskPerShare
///
/// If RiskPerShare &lt;= 0, the trade is rejected outright — never
/// sign-flipped or floored to a nominal value (spec §4.1, explicit).
///
/// Final size = MIN of 9 candidate quantities (the spec's literal 8, plus
/// CorrelationConstrainedQty — see PositionSizingResult's XML doc for why
/// that 9th one is a justified addition, not a deviation). Every candidate
/// is computed and retained on the result even when it doesn't bind, so a
/// test can independently prove which constraint was actually tightest
/// (spec §4.1: "Log which constraint(s) actually bound... required for the
/// unit tests in §8.1").
///
/// Microcap gate (spec §4.2): "cannot bypass portfolio risk rules" — these
/// are hard REJECT conditions checked before any quantity math, not just
/// another quantity constraint to take the min of. A microcap candidate
/// missing a required score (null, not merely low) fails the gate exactly
/// like a score below threshold would — missing data is never treated as
/// a pass.
/// </summary>
public static class PositionSizingCalculator
{
    public static PositionSizingResult Calculate(PositionSizingInputs inputs, RiskOptions riskOptions, PortfolioOptions portfolioOptions)
    {
        var riskPerShare = inputs.EntryPrice - inputs.StopPrice;
        if (riskPerShare <= 0)
        {
            return Rejected(inputs.Symbol, $"RiskPerShare <= 0 (EntryPrice {inputs.EntryPrice}, StopPrice {inputs.StopPrice}) — entry likely gapped through the stop. Never sign-flipped or floored.");
        }

        if (inputs.MarketCapBucket == MarketCapBucket.Micro)
        {
            var gateFailure = EvaluateMicrocapGate(inputs, riskOptions);
            if (gateFailure is not null) return Rejected(inputs.Symbol, gateFailure);
        }

        const decimal notApplicableSentinel = 999_999_999m;

        var riskBasedQty = Floor(inputs.Nav * riskOptions.RiskPercentPerTrade / 100m / riskPerShare);

        var positionCapPercent = inputs.StockScoreTotalScore >= riskOptions.ExceptionalPositionScoreThreshold
            ? portfolioOptions.ExceptionalPositionPercent
            : portfolioOptions.MaxSinglePositionPercent;
        var maxSinglePositionQty = Floor(inputs.Nav * positionCapPercent / 100m / inputs.EntryPrice);

        var liquidityConstrainedQty = Floor(inputs.AverageDailyTradedValue * riskOptions.MaxParticipationPercentOfAdtv / 100m / inputs.EntryPrice);

        var sectorHeadroom = Math.Max(0m, inputs.Nav * portfolioOptions.MaxSectorExposurePercent / 100m - inputs.CurrentSectorExposureValue);
        var sectorConstrainedQty = Floor(sectorHeadroom / inputs.EntryPrice);

        var microcapConstrainedQty = inputs.MarketCapBucket == MarketCapBucket.Micro
            ? Floor(Math.Max(0m, inputs.Nav * portfolioOptions.MaxMicrocapExposurePercent / 100m - inputs.CurrentMicrocapExposureValue) / inputs.EntryPrice)
            : notApplicableSentinel;

        var cashHeadroom = Math.Max(0m, inputs.CashBalance - inputs.Nav * portfolioOptions.MinimumCashPercent / 100m);
        var cashConstrainedQty = Floor(cashHeadroom / inputs.EntryPrice);

        var regimeAdjustedQty = Floor(riskBasedQty * inputs.DeployableCapitalPercent / 100m);

        var heatHeadroom = Math.Max(0m, inputs.Nav * riskOptions.PortfolioHeatMaxPercent / 100m - inputs.CurrentPortfolioHeatValue);
        var portfolioHeatConstrainedQty = Floor(heatHeadroom / riskPerShare);

        var correlationHeadroom = Math.Max(0m, inputs.Nav * riskOptions.MaxCorrelatedClusterExposurePercent / 100m - inputs.CorrelatedClusterExposureValue);
        var correlationConstrainedQty = Floor(correlationHeadroom / inputs.EntryPrice);

        var candidates = new (string Name, decimal Qty)[]
        {
            (nameof(PositionSizingResult.RiskBasedQty), riskBasedQty),
            (nameof(PositionSizingResult.MaxSinglePositionQty), maxSinglePositionQty),
            (nameof(PositionSizingResult.LiquidityConstrainedQty), liquidityConstrainedQty),
            (nameof(PositionSizingResult.SectorConstrainedQty), sectorConstrainedQty),
            (nameof(PositionSizingResult.MicrocapConstrainedQty), microcapConstrainedQty),
            (nameof(PositionSizingResult.CashConstrainedQty), cashConstrainedQty),
            (nameof(PositionSizingResult.RegimeAdjustedQty), regimeAdjustedQty),
            (nameof(PositionSizingResult.PortfolioHeatConstrainedQty), portfolioHeatConstrainedQty),
            (nameof(PositionSizingResult.CorrelationConstrainedQty), correlationConstrainedQty),
        };

        var finalQty = candidates.Min(c => c.Qty);
        var bindingConstraints = candidates.Where(c => c.Qty == finalQty).Select(c => c.Name).ToList();

        var result = new PositionSizingResult
        {
            Symbol = inputs.Symbol,
            RiskBasedQty = riskBasedQty,
            MaxSinglePositionQty = maxSinglePositionQty,
            LiquidityConstrainedQty = liquidityConstrainedQty,
            SectorConstrainedQty = sectorConstrainedQty,
            MicrocapConstrainedQty = microcapConstrainedQty,
            CashConstrainedQty = cashConstrainedQty,
            RegimeAdjustedQty = regimeAdjustedQty,
            PortfolioHeatConstrainedQty = portfolioHeatConstrainedQty,
            CorrelationConstrainedQty = correlationConstrainedQty,
            FinalQty = Math.Max(0m, finalQty),
            BindingConstraints = bindingConstraints,
            Rejected = finalQty <= 0,
            RejectionReason = finalQty <= 0 ? $"No room under binding constraint(s): {string.Join(", ", bindingConstraints)}" : null,
        };

        return result;
    }

    private static string? EvaluateMicrocapGate(PositionSizingInputs inputs, RiskOptions options)
    {
        if (inputs.TechnicalScore is not { } technical || technical < options.MicrocapMinTechnicalScore)
            return $"Microcap gate failed: TechnicalScore {(inputs.TechnicalScore is null ? "unavailable" : inputs.TechnicalScore.ToString())} < required {options.MicrocapMinTechnicalScore}.";

        if (inputs.FundamentalScore is not { } fundamental || fundamental < options.MicrocapMinFundamentalScore)
            return $"Microcap gate failed: FundamentalScore {(inputs.FundamentalScore is null ? "unavailable" : inputs.FundamentalScore.ToString())} < required {options.MicrocapMinFundamentalScore}.";

        if (inputs.HasCriticalSurveillanceFlag)
            return "Microcap gate failed: a critical surveillance flag is set.";

        if (inputs.HasSevereDataQualityIssue)
            return "Microcap gate failed: a severe data-quality issue is flagged.";

        return null;
    }

    private static PositionSizingResult Rejected(string symbol, string reason) => new()
    {
        Symbol = symbol,
        BindingConstraints = Array.Empty<string>(),
        Rejected = true,
        RejectionReason = reason,
    };

    private static decimal Floor(decimal value) => Math.Floor(value);
}
