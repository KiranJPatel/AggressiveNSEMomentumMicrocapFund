namespace TradingPlatform.Risk.Trailing;

/// <summary>The active tier of the trailing-stop ladder (spec §4.3): Initial -> Breakeven -> AtrTrailing -> AggressiveWinnerProtection.</summary>
public enum TrailingStage { Initial, Breakeven, AtrTrailing, AggressiveWinnerProtection }

public sealed record TrailingStopResult(TrailingStage Stage, decimal NewStop, decimal RMultiple);
