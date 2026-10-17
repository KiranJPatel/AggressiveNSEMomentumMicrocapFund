using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>Minimal HistoricalBar builder for Backtesting-layer tests, which mostly care about a single day's OHLCV rather than a multi-day series.</summary>
public static class BacktestBarFixture
{
    public static HistoricalBar Bar(decimal open, decimal high, decimal low, decimal close, long volume, string symbol = "TESTSYM", DateTimeOffset? timestamp = null) => new()
    {
        Symbol = symbol,
        Timestamp = timestamp ?? new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(5.5)),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        AdjustedClose = close,
        Volume = volume,
        Source = "Fixture",
        Timeframe = Timeframe.Daily,
    };
}
