namespace TradingPlatform.Core.Exceptions;

/// <summary>
/// Thrown when broker state and local SQLite state disagree (spec §6.4).
/// Trading must halt and require manual intervention — never auto-guess a fix.
/// </summary>
public sealed class ReconciliationMismatchException : TradingPlatformException
{
    public ReconciliationMismatchException(string message) : base(message) { }
}
