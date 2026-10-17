using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Backtesting.Engine;

/// <summary>Every config section BacktestEngine's daily loop touches, bundled so BacktestEngine.Run doesn't need nine separate parameters.</summary>
public sealed class BacktestEngineOptions
{
    public required IndicatorOptions Indicator { get; init; }
    public required VcpOptions Vcp { get; init; }
    public required RsOptions Rs { get; init; }
    public required StockScoringOptions StockScoring { get; init; }
    public required StrategyOptions Strategy { get; init; }
    public required MarketRegimeOptions MarketRegime { get; init; }
    public required ExitOptions Exit { get; init; }
    public required RiskOptions Risk { get; init; }
    public required PortfolioOptions Portfolio { get; init; }
    public required BacktestOptions Backtest { get; init; }
}
