namespace TradingPlatform.Core.Abstractions;

/// <summary>
/// Clock abstraction so Backtest/Paper/Live differ only in DataProvider,
/// ExecutionProvider, and Clock (spec §1.2, §5.6) — never in strategy logic.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
