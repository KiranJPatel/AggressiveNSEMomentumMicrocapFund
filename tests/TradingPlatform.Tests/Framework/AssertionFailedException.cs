namespace TradingPlatform.Tests.Framework;

public sealed class AssertionFailedException : Exception
{
    public AssertionFailedException(string message) : base(message) { }
}
