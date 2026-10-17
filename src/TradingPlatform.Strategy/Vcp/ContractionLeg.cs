using TradingPlatform.Indicators.Structure;

namespace TradingPlatform.Strategy.Vcp;

/// <summary>One completed High-to-Low pullback ("contraction") within a candidate VCP base (spec §3.5).</summary>
public sealed record ContractionLeg(SwingPoint SwingHigh, SwingPoint SwingLow, decimal DepthPercent, decimal AverageVolume);
