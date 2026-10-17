using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;

namespace TradingPlatform.Backtesting.Costs;

/// <summary>Per-fill transaction cost breakdown (spec §5.4). Every component is retained, not just Total, so a report can show what actually ate into a trade's return.</summary>
public sealed record TransactionCostBreakdown(
    decimal Brokerage,
    decimal Stt,
    decimal ExchangeCharges,
    decimal Gst,
    decimal SebiCharges,
    decimal StampDuty,
    decimal Total);

/// <summary>
/// Implements spec §5.4's cost model: "brokerage, STT, exchange charges,
/// GST, SEBI charges, stamp duty ... live in config, not hard-coded."
/// CommissionOptions (Tranche 0) already holds every rate, each flagged
/// `RatesVerifiedCurrent = false` until manually confirmed against a live
/// broker/exchange schedule — this calculator's job is just the formula
/// structure, which is standard for Indian equity DELIVERY trades
/// regardless of which specific rates apply:
///
///   - Brokerage: percentage of turnover, capped per order (already the
///     shape of CommissionOptions.BrokeragePercent/BrokerageMaxPerOrder).
///   - STT (Securities Transaction Tax): charged on BOTH buy and sell for
///     delivery equity — unlike intraday, where it's sell-side only. This
///     platform is long-only delivery (spec §2.2), so both-sides is the
///     correct convention here, not a redundant double-charge.
///   - Exchange transaction charges: percentage of turnover, both sides.
///   - GST: applies to (brokerage + exchange charges) only — GST is a tax
///     on the service fee, not on the trade's regulatory levies (STT,
///     SEBI charges, stamp duty are themselves statutory charges, not
///     brokerage-service consideration, so GST does not compound onto
///     them). A resolved ambiguity worth naming even though it reflects
///     standard practice, since compounding GST onto every component
///     would be a real, silent overstatement of cost.
///   - SEBI charges: tiny percentage of turnover, both sides.
///   - Stamp duty: BUY side only — the Finance Act 2019 uniform stamp-duty
///     rules collect it once, on the purchase leg, not on the sale leg.
/// </summary>
public static class TransactionCostCalculator
{
    public static TransactionCostBreakdown Calculate(decimal turnoverValue, OrderSide side, CommissionOptions options)
    {
        if (turnoverValue < 0) throw new ArgumentException("turnoverValue must not be negative.", nameof(turnoverValue));

        var brokerage = Math.Min(turnoverValue * options.BrokeragePercent / 100m, options.BrokerageMaxPerOrder);
        var stt = turnoverValue * options.SttPercent / 100m;
        var exchangeCharges = turnoverValue * options.ExchangeChargesPercent / 100m;
        var gst = (brokerage + exchangeCharges) * options.GstPercent / 100m;
        var sebiCharges = turnoverValue * options.SebiChargesPercent / 100m;
        var stampDuty = side == OrderSide.Buy ? turnoverValue * options.StampDutyPercent / 100m : 0m;

        var total = brokerage + stt + exchangeCharges + gst + sebiCharges + stampDuty;
        return new TransactionCostBreakdown(brokerage, stt, exchangeCharges, gst, sebiCharges, stampDuty, total);
    }
}
