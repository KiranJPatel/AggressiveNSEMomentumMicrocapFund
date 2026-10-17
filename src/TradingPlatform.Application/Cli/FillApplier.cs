using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// What an order was FOR, persisted at submission time so a fill that arrives in a LATER cycle (live: Kite returns
/// "Submitted", the fill is only learned by polling) can still be turned into the right position/trade. Paper fills
/// arrive in the same cycle and use the same record, so there is exactly one fill-application path.
/// </summary>
public sealed record OrderIntent(
    string Kind,                 // "Entry" (Stage 1) | "Add" (Stage 2/3) | "Exit"
    string Symbol,
    decimal PivotPrice,
    decimal SuggestedEntry,
    decimal SuggestedStop,
    decimal SuggestedPositionSize,
    ExitReason? ExitReason,
    PyramidStage? Stage = null,
    decimal TotalTargetQuantity = 0m,
    // Journal context captured when the decision was made (spec §7.2 immutable trade journal).
    int RegimeScoreAtEntry = 0,
    decimal StockScoreAtEntry = 0m,
    decimal RiskAtEntryPercent = 0m,
    decimal MfeR = 0m,   // exits only: max favourable excursion in R (same definition as the backtest)
    decimal MaeR = 0m)   // exits only: max adverse excursion in R (<= 0)
{
    public static string StateKey(string correlationId) => $"OrderIntent:{correlationId}";
}

/// <summary>The single place a fill becomes cash / Fill row / Position / Trade (paper same-cycle AND live synced).</summary>
public static class FillApplier
{
    /// <summary>Applies one fill and returns the CASH DELTA (negative for buys). Never throws on a missing position — it logs and returns 0.</summary>
    public static async Task<decimal> ApplyAsync(
        Order o, long orderId, OrderIntent intent, Position? exitPosition,
        IOrderRepository orderRepo, IPositionRepository positionRepo, ITradeRepository tradeRepo,
        ISignalRepository signalRepo, IRiskEventRepository riskEventRepo, IEngineStateRepository engineState, CancellationToken cancellationToken)
    {
        var filledQty = o.FilledQuantity > 0 ? o.FilledQuantity : o.Quantity;
        var commission = o.FillCommission;
        var isExit = intent.Kind == "Exit";
        var filledAt = DateTimeOffset.UtcNow;

        if (isExit && exitPosition is null) return 0m;

        var plannedPrice = isExit ? exitPosition!.AverageEntryPrice : intent.SuggestedEntry;
        var fillPrice = o.AverageFillPrice ?? plannedPrice;
        if (o.AverageFillPrice is null)
            Console.Error.WriteLine($"WARNING: {o.Symbol} {o.Side} reported filled without a fill price — falling back to {plannedPrice:N2}. P&L for this trade is approximate.");

        await orderRepo.RecordFillAsync(new Fill
        {
            OrderId = orderId, Symbol = o.Symbol, Quantity = filledQty, Price = fillPrice,
            SlippageAmount = o.FillSlippageAmount, Commission = commission, FilledAt = filledAt,
        }, cancellationToken);

        if (isExit)
        {
            var position = exitPosition!;
            var reason = intent.ExitReason ?? ExitReason.StopLoss;
            var quantityBeforeExit = position.Quantity; // captured BEFORE a partial exit shrinks the position below
            var closesFully = filledQty >= quantityBeforeExit;

            if (closesFully) await positionRepo.CloseAsync(position.Id, filledAt, reason, cancellationToken);
            else
            {
                position.Quantity -= filledQty;
                await positionRepo.UpsertAsync(position, cancellationToken);
            }

            // Entry-time journal context + entry-leg commission (proportional to the slice being exited) from the staging record.
            var stagingJsonX = await engineState.GetAsync(PositionStaging.StateKey(position.Symbol), cancellationToken);
            var stagingX = stagingJsonX is null ? null : System.Text.Json.JsonSerializer.Deserialize<PositionStaging>(stagingJsonX);
            var fraction = quantityBeforeExit > 0 ? Math.Min(1m, filledQty / quantityBeforeExit) : 1m;
            var entryCommissionShare = (stagingX?.EntryCommission ?? 0m) * (closesFully ? 1m : fraction);
            if (!closesFully && stagingX is not null)
                await engineState.SetAsync(PositionStaging.StateKey(position.Symbol), System.Text.Json.JsonSerializer.Serialize(stagingX with { EntryCommission = stagingX.EntryCommission - entryCommissionShare }), cancellationToken);

            var trade = new Trade
            {
                Symbol = position.Symbol, EntryPrice = position.AverageEntryPrice, InitialStop = position.InitialStop,
                InitialRiskAmount = position.InitialRiskAmount, ExitPrice = fillPrice, ExitReason = reason,
                // Net of BOTH legs' commission, so per-trade P&L reconciles with cash.
                RealizedPnl = filledQty * (fillPrice - position.AverageEntryPrice) - commission - entryCommissionShare,
                RMultiple = position.RiskPerShare > 0 ? (fillPrice - position.AverageEntryPrice) / position.RiskPerShare : 0m,
                MaximumFavorableExcursion = intent.MfeR, MaximumAdverseExcursion = intent.MaeR,
                RegimeScoreAtEntry = stagingX?.RegimeScoreAtEntry ?? 0, StockScoreAtEntry = stagingX?.StockScoreAtEntry ?? 0m,
                RiskAtEntryPercent = stagingX?.RiskAtEntryPercent ?? 0m,
                StrategyVersion = "AggressiveMomentumV1-Tranche20", ConfigurationVersion = "live",
                EntryAt = position.OpenedAt, ExitAt = filledAt,
            };
            var leg = new TradeLeg { Stage = position.HighestStageFilled, Quantity = filledQty, Price = position.AverageEntryPrice, FilledAt = position.OpenedAt };
            await tradeRepo.InsertAsync(trade, new[] { leg }, cancellationToken);

            await riskEventRepo.InsertAsync(new RiskEvent
            {
                EventType = closesFully ? "PositionClosed" : "PositionReduced",
                Description = $"{position.Symbol} {(closesFully ? "closed" : $"reduced by {filledQty:N0}")} via {reason} @ {fillPrice:N2}.",
                Symbol = position.Symbol, Timestamp = filledAt, CorrelationId = o.CorrelationId,
            }, cancellationToken);
            return filledQty * fillPrice - commission;
        }

        if (intent.Kind == "Add")
        {
            // Pyramid add: grow the EXISTING position (weighted-average entry), advance its stage, record the stage fill.
            var existing = await positionRepo.GetOpenBySymbolAsync(intent.Symbol, cancellationToken);
            if (existing is null)
            {
                Console.Error.WriteLine($"WARNING: {intent.Symbol} add filled but no open position exists — not applied; reconcile manually.");
                return -(filledQty * fillPrice + commission);
            }
            var newQty = existing.Quantity + filledQty;
            existing.AverageEntryPrice = (existing.Quantity * existing.AverageEntryPrice + filledQty * fillPrice) / newQty;
            existing.Quantity = newQty;
            existing.HighestStageFilled = intent.Stage ?? existing.HighestStageFilled;
            await positionRepo.UpsertAsync(existing, cancellationToken);

            var stagingJson = await engineState.GetAsync(PositionStaging.StateKey(intent.Symbol), cancellationToken);
            var staging = stagingJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<PositionStaging>(stagingJson);
            if (staging is not null)
            {
                var updated = staging with { EntryCommission = staging.EntryCommission + commission };
                if (intent.Stage == PyramidStage.Stage2Add) updated = updated with { Stage2EntryPrice = fillPrice, Stage2EntryTimestamp = filledAt };
                await engineState.SetAsync(PositionStaging.StateKey(intent.Symbol), System.Text.Json.JsonSerializer.Serialize(updated), cancellationToken);
            }

            await riskEventRepo.InsertAsync(new RiskEvent
            {
                EventType = "PyramidAdd", Description = $"{intent.Symbol} {(intent.Stage?.ToString() ?? "entry top-up")} +{filledQty:N0} @ {fillPrice:N2} (position now {newQty:N0} @ avg {existing.AverageEntryPrice:N2}).",
                Symbol = intent.Symbol, Timestamp = filledAt, CorrelationId = o.CorrelationId,
            }, cancellationToken);
            return -(filledQty * fillPrice + commission);
        }

        var riskPerShare = Math.Max(fillPrice - intent.SuggestedStop, 0.01m);
        await signalRepo.InsertAsync(new Signal
        {
            Symbol = intent.Symbol, GeneratedAt = filledAt, Stage = PyramidStage.Stage1Initial,
            PivotPrice = intent.PivotPrice, SuggestedEntry = intent.SuggestedEntry, SuggestedStop = intent.SuggestedStop,
            RiskPerShare = intent.SuggestedEntry - intent.SuggestedStop, SuggestedPositionQty = intent.SuggestedPositionSize,
            SignalToOrderLag = SignalToOrderLag.NextOpen, StrategyVersion = "AggressiveMomentumV1-Tranche20", ConfigurationVersion = "live",
        }, cancellationToken);

        await positionRepo.UpsertAsync(new Position
        {
            Symbol = intent.Symbol, Quantity = filledQty, AverageEntryPrice = fillPrice,
            InitialStop = intent.SuggestedStop, CurrentStop = intent.SuggestedStop, InitialRiskAmount = filledQty * riskPerShare,
            RiskPerShare = riskPerShare, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true, OpenedAt = filledAt,
        }, cancellationToken);

        // Remember the TOTAL target so Stage 2/3 adds (possibly in later cycles/restarts) use the original sizing split.
        // Written even for a non-staged target so exits still find the entry-time journal context and entry commission.
        await engineState.SetAsync(PositionStaging.StateKey(intent.Symbol),
            System.Text.Json.JsonSerializer.Serialize(new PositionStaging(intent.TotalTargetQuantity, fillPrice, filledAt,
                RegimeScoreAtEntry: intent.RegimeScoreAtEntry, StockScoreAtEntry: intent.StockScoreAtEntry,
                RiskAtEntryPercent: intent.RiskAtEntryPercent, EntryCommission: commission)), cancellationToken);
        return -(filledQty * fillPrice + commission);
    }
}
