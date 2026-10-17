namespace TradingPlatform.Core.Exceptions;

/// <summary>Base type for all platform-specific exceptions.</summary>
public class TradingPlatformException : Exception
{
    public TradingPlatformException(string message) : base(message) { }
    public TradingPlatformException(string message, Exception innerException) : base(message, innerException) { }
}
