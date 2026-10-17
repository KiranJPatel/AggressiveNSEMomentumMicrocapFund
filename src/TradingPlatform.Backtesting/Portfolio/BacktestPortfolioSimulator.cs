using TradingPlatform.Backtesting.Costs;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Portfolio;

/// <summary>
/// Applies simulated fills to a BacktestPortfolioState (spec §5.1).
/// </summary>
public static class BacktestPortfolioSimulator
{
    /// <summary>
    /// Applies a BUY fill (Stage 1/2/3, spec §3.7). For a symbol with no
    /// existing lot, the four "NewPosition" parameters establish the
    /// position's fixed InitialStop/RiskPerShare/InitialRiskAmount (see
    /// OpenPositionLot's remarks on why these never change on later
    /// stages) and are required; for an add to an existing lot they are
    /// ignored (the existing lot's own fixed values govern).
    /// </summary>
    public static void ApplyBuyFill(
        BacktestPortfolioState state,
        string symbol,
        PyramidStage stage,
        decimal quantity,
        decimal price,
        TransactionCostBreakdown costs,
        DateTimeOffset filledAt,
        decimal? newPositionInitialStop = null,
        decimal? newPositionRiskPerShare = null,
        decimal? newPositionInitialRiskAmount = null,
        decimal? newPositionTotalTargetQuantity = null,
        long? newPositionSignalId = null,
        int newPositionRegimeScoreAtEntry = 0,
        decimal newPositionStockScoreAtEntry = 0m,
        decimal newPositionRiskAtEntryPercent = 0m,
        string newPositionStrategyVersion = "",
        string newPositionConfigurationVersion = "")
    {
        if (quantity <= 0) throw new ArgumentException("quantity must be positive.", nameof(quantity));

        var cashOut = price * quantity + costs.Total;
        state.Cash -= cashOut;

        if (!state.OpenPositions.TryGetValue(symbol, out var lot))
        {
            if (newPositionInitialStop is null || newPositionRiskPerShare is null || newPositionInitialRiskAmount is null || newPositionTotalTargetQuantity is null)
                throw new InvalidOperationException($"Opening a new position in {symbol} requires InitialStop/RiskPerShare/InitialRiskAmount/TotalTargetQuantity.");

            lot = new OpenPositionLot
            {
                Symbol = symbol,
                InitialStop = newPositionInitialStop.Value,
                CurrentStop = newPositionInitialStop.Value,
                RiskPerShare = newPositionRiskPerShare.Value,
                InitialRiskAmount = newPositionInitialRiskAmount.Value,
                TotalTargetQuantity = newPositionTotalTargetQuantity.Value,
                OpenedAt = filledAt,
                HighestPriceSinceEntry = price,
                LowestPriceSinceEntry = price,
                SignalId = newPositionSignalId,
                RegimeScoreAtEntry = newPositionRegimeScoreAtEntry,
                StockScoreAtEntry = newPositionStockScoreAtEntry,
                RiskAtEntryPercent = newPositionRiskAtEntryPercent,
                StrategyVersion = newPositionStrategyVersion,
                ConfigurationVersion = newPositionConfigurationVersion,
            };
            state.OpenPositions[symbol] = lot;
        }

        var newTotalPriceCost = lot.AverageEntryPrice * lot.Quantity + price * quantity;
        lot.Quantity += quantity;
        lot.AverageEntryPrice = lot.Quantity > 0 ? newTotalPriceCost / lot.Quantity : 0m;
        lot.TotalCostBasisIncludingFees += cashOut;
        lot.HighestStageFilled = stage;

        switch (stage)
        {
            case PyramidStage.Stage1Initial:
                lot.Stage1EntryPrice = price;
                lot.Stage1EntryTimestamp = filledAt;
                break;
            case PyramidStage.Stage2Add:
                lot.Stage2EntryPrice = price;
                lot.Stage2EntryTimestamp = filledAt;
                break;
        }

        lot.Legs.Add(new TradeLeg { Stage = stage, Quantity = quantity, Price = price, FilledAt = filledAt });
    }

    /// <summary>Marks a position to market against today's High/Low (not just Close) so MFE/MAE capture the true intraday extremes — call once per day for every open position, before any exit/trailing decision.</summary>
    public static void UpdateExcursion(OpenPositionLot lot, decimal barHigh, decimal barLow)
    {
        if (barHigh > lot.HighestPriceSinceEntry) lot.HighestPriceSinceEntry = barHigh;
        if (barLow < lot.LowestPriceSinceEntry) lot.LowestPriceSinceEntry = barLow;
    }

    /// <summary>
    /// Applies a SELL fill — spec §5.1/§5.4's partial-fill support applies
    /// here too, not just on entries: if BacktestFillSimulator's liquidity
    /// cap only allows part of the requested exit quantity to fill, that
    /// slice is realized immediately and the remainder stays open (still
    /// tracked, stop still active) for the engine to try again on a later
    /// day — exactly like a real large exit that takes more than one
    /// session to fully unwind. A Trade record is produced ONLY once the
    /// position is fully closed (quantitySold brings Quantity to zero) —
    /// partial exits never appear as a standalone Trade of their own,
    /// since "the trade" is the whole entry-to-flat lifecycle, and
    /// PositionSizingCalculator's ADTV-aware sizing at entry time (Tranche
    /// 8) already keeps this a genuine edge case rather than the common
    /// path — most positions this system takes are small enough, relative
    /// to their own liquidity, to exit in a single day.
    /// </summary>
    public static SellFillOutcome ApplySellFill(
        BacktestPortfolioState state,
        string symbol,
        decimal quantitySold,
        decimal price,
        TransactionCostBreakdown costs,
        DateTimeOffset filledAt,
        ExitReason exitReason)
    {
        if (!state.OpenPositions.TryGetValue(symbol, out var lot))
            throw new InvalidOperationException($"No open position in {symbol} to sell.");
        if (quantitySold <= 0 || quantitySold > lot.Quantity)
            throw new ArgumentException($"quantitySold must be in (0, {lot.Quantity}] for {symbol}.", nameof(quantitySold));

        var proceeds = price * quantitySold - costs.Total;
        state.Cash += proceeds;

        var costBasisForSlice = lot.Quantity > 0 ? lot.TotalCostBasisIncludingFees * (quantitySold / lot.Quantity) : 0m;
        var realizedPnlForSlice = proceeds - costBasisForSlice;

        lot.RealizedPnlFromPartialExits += realizedPnlForSlice;
        lot.TotalCostBasisIncludingFees -= costBasisForSlice;
        lot.Quantity -= quantitySold;
        lot.ExitFills.Add(new ExitFillRecord(quantitySold, price, filledAt, exitReason));

        if (lot.Quantity > 0)
            return SellFillOutcome.Partial(realizedPnlForSlice);

        state.OpenPositions.Remove(symbol);

        var totalExitQty = lot.ExitFills.Sum(f => f.Quantity);
        var weightedAvgExitPrice = lot.ExitFills.Sum(f => f.Quantity * f.Price) / totalExitQty;
        var finalReason = lot.ExitFills[^1].Reason; // whichever slice actually finished the position off

        var realizedPnl = lot.RealizedPnlFromPartialExits;
        var rMultiple = lot.InitialRiskAmount > 0 ? realizedPnl / lot.InitialRiskAmount : 0m;
        var mfeR = lot.RiskPerShare > 0 ? (lot.HighestPriceSinceEntry - lot.AverageEntryPrice) / lot.RiskPerShare : 0m;
        var maeR = lot.RiskPerShare > 0 ? (lot.LowestPriceSinceEntry - lot.AverageEntryPrice) / lot.RiskPerShare : 0m;

        var tradeId = state.AllocateTradeId();
        var trade = new Trade
        {
            Id = tradeId,
            Symbol = symbol,
            SignalId = lot.SignalId,
            EntryPrice = lot.AverageEntryPrice,
            InitialStop = lot.InitialStop,
            InitialRiskAmount = lot.InitialRiskAmount,
            ExitPrice = weightedAvgExitPrice,
            ExitReason = finalReason,
            RealizedPnl = realizedPnl,
            RMultiple = rMultiple,
            MaximumFavorableExcursion = mfeR,
            MaximumAdverseExcursion = maeR,
            RegimeScoreAtEntry = lot.RegimeScoreAtEntry,
            StockScoreAtEntry = lot.StockScoreAtEntry,
            RiskAtEntryPercent = lot.RiskAtEntryPercent,
            StrategyVersion = lot.StrategyVersion,
            ConfigurationVersion = lot.ConfigurationVersion,
            EntryAt = lot.OpenedAt,
            ExitAt = lot.ExitFills[^1].FilledAt,
        };

        state.ClosedTrades.Add(trade);
        foreach (var leg in lot.Legs)
        {
            leg.TradeId = tradeId;
            state.ClosedTradeLegs.Add(leg);
        }

        return SellFillOutcome.Full(trade);
    }
}

/// <summary>Whether a sell fill fully closed the position (Trade is populated) or only partially exited it (position stays open, Trade is null).</summary>
public sealed record SellFillOutcome(bool FullyClosed, decimal RealizedPnl, Trade? Trade)
{
    public static SellFillOutcome Partial(decimal realizedPnl) => new(false, realizedPnl, null);
    public static SellFillOutcome Full(Trade trade) => new(true, trade.RealizedPnl, trade);
}
