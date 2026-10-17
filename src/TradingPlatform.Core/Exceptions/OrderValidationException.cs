namespace TradingPlatform.Core.Exceptions;

/// <summary>Thrown when pre-trade validation fails (spec §6.3).</summary>
public sealed class OrderValidationException : TradingPlatformException
{
    public OrderValidationException(string message) : base(message) { }
}
