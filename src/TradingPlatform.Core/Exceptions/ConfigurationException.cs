namespace TradingPlatform.Core.Exceptions;

/// <summary>Thrown when required configuration is missing or invalid at startup.</summary>
public sealed class ConfigurationException : TradingPlatformException
{
    public ConfigurationException(string message) : base(message) { }
}
