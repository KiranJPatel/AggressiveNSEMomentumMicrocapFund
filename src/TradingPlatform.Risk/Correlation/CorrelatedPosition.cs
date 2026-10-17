using TradingPlatform.Core.Models;

namespace TradingPlatform.Risk.Correlation;

/// <summary>An existing open position's data needed for correlation-cluster analysis.</summary>
public sealed record CorrelatedPosition(string Symbol, IReadOnlyList<HistoricalBar> Bars, decimal PositionValue);
