using TradingPlatform.Core.Enums;

namespace TradingPlatform.Strategy.Exit;

/// <summary>One of the four exit mechanisms (spec §4.3), evaluated independently — unlike Breakout's AND-gate, ANY firing means exit (an OR-gate).</summary>
public sealed record ExitMechanismResult(ExitReason Reason, bool Fired, string Detail);

public sealed record ExitEvaluation(bool ShouldExit, ExitReason? PrimaryReason, IReadOnlyList<ExitMechanismResult> Mechanisms);
