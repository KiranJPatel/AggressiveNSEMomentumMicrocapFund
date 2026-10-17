using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Regime;

namespace TradingPlatform.Scanner;

public sealed class ScannerEngineOptions
{
    public required ScannerOptions Scanner { get; init; }
    public required IndicatorOptions Indicator { get; init; }
    public required VcpOptions Vcp { get; init; }
    public required RsOptions Rs { get; init; }
    public required StockScoringOptions StockScoring { get; init; }
    public required StrategyOptions Strategy { get; init; }
    public required MarketRegimeOptions MarketRegime { get; init; }
    public required RiskOptions Risk { get; init; }
    public required PortfolioOptions Portfolio { get; init; }

    /// <summary>Closed trades from the live/paper journal, for the regime's breakout-success-rate input (spec §3.1). Null/empty = that component is unavailable (never assumed good).</summary>
    public IReadOnlyList<Trade>? RecentClosedTrades { get; init; }

    /// <summary>Universe-wide breadth computed by the caller over the FULL universe. Needed when the scanner is handed only a shortlist (the real-time re-rank pass), where deriving breadth from the shortlist would be wrong. Null = derive from the symbols passed to Scan.</summary>
    public UniverseBreadth? PrecomputedBreadth { get; init; }

    /// <summary>Copy of these options carrying the given closed-trade history (the options type is immutable-by-init, so callers cannot just set the property).</summary>
    public ScannerEngineOptions WithRecentClosedTrades(IReadOnlyList<Trade>? trades) => new()
    {
        Scanner = Scanner, Indicator = Indicator, Vcp = Vcp, Rs = Rs, StockScoring = StockScoring, Strategy = Strategy,
        MarketRegime = MarketRegime, Risk = Risk, Portfolio = Portfolio, RecentClosedTrades = trades, PrecomputedBreadth = PrecomputedBreadth,
    };

    public ScannerEngineOptions WithPrecomputedBreadth(UniverseBreadth? breadth) => new()
    {
        Scanner = Scanner, Indicator = Indicator, Vcp = Vcp, Rs = Rs, StockScoring = StockScoring, Strategy = Strategy,
        MarketRegime = MarketRegime, Risk = Risk, Portfolio = Portfolio, RecentClosedTrades = RecentClosedTrades, PrecomputedBreadth = breadth,
    };
}
