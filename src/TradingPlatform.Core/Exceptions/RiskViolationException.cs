namespace TradingPlatform.Core.Exceptions;

/// <summary>Thrown when an action would violate a hard risk control (spec §0.4: never weaken risk controls).</summary>
public sealed class RiskViolationException : TradingPlatformException
{
    public RiskViolationException(string message) : base(message) { }
}
