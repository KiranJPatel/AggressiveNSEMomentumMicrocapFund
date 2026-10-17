using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Portfolio;

/// <summary>
/// The running cash/positions ledger for one backtest (spec §5.1: "cash,
/// orders, fills"). Deliberately mutable — unlike DrawdownState/
/// LossLimitState (Tranche 10), which are small threaded call-to-call
/// VALUES, this is a large, continuously-updated ledger that's more
/// naturally an owned, stateful object than an immutable record rebuilt
/// every day.
/// </summary>
public sealed class BacktestPortfolioState
{
    public required decimal Cash { get; set; }
    public Dictionary<string, OpenPositionLot> OpenPositions { get; } = new();
    public List<Trade> ClosedTrades { get; } = new();
    public List<TradeLeg> ClosedTradeLegs { get; } = new();

    private long _nextTradeId = 1;
    internal long AllocateTradeId() => _nextTradeId++;

    public static BacktestPortfolioState Initial(decimal initialCapital) => new() { Cash = initialCapital };
}
