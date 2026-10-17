namespace TradingPlatform.Core.Exceptions;

/// <summary>Thrown when input data fails validation (spec §5.3) — never silently "fixed".</summary>
public sealed class DataQualityException : TradingPlatformException
{
    public DataQualityException(string message) : base(message) { }
}
