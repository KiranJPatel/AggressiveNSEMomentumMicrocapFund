using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Application.Cli;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class IntradayContextLoaderTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static HistoricalBar Bar(string symbol, DateOnly d, int h, int m, Timeframe tf) => new()
    {
        Symbol = symbol, Timestamp = new DateTimeOffset(d.ToDateTime(new TimeOnly(h, m)), Ist), Open = 100, High = 101, Low = 99, Close = 100, AdjustedClose = 100,
        Volume = 500, Source = "t", Timeframe = tf, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BacktestSymbolContext Ctx(string symbol) => new() { Symbol = symbol, Bars = BacktestEngineFixture.BuildStockBars(), MarketCapBucket = MarketCapBucket.Mid };

    [Fact]
    public async Task NextOpen_IsANoOp_ReturningTheSameContextsAndNoCoverage()
    {
        using var host = CliTestHost.Create();
        var symbols = new[] { Ctx("AAA") };

        var (result, coverage) = await IntradayContextLoader.AttachAsync(symbols, host.Repo<IHistoricalBarRepository>(), SignalToOrderLag.NextOpen,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1), NullLogger.Instance);

        Assert.True(ReferenceEquals(symbols, result), "NextOpen must return the very same list, untouched");
        Assert.Null(coverage);
    }

    [Fact]
    public async Task Refinement15m_AttachesCachedBars_AndReportsHowMuchOfTheUniverseIsCovered()
    {
        using var host = CliTestHost.Create();
        var bars = host.Repo<IHistoricalBarRepository>();
        var d1 = new DateOnly(2026, 2, 10); var d2 = new DateOnly(2026, 2, 11);
        await bars.UpsertRangeAsync(new[]
        {
            Bar("AAA", d1, 9, 15, Timeframe.H1), Bar("AAA", d1, 10, 15, Timeframe.H1), Bar("AAA", d2, 9, 15, Timeframe.H1),
            Bar("AAA", d1, 9, 15, Timeframe.M15), Bar("AAA", d2, 9, 30, Timeframe.M15),
        });

        var (result, coverage) = await IntradayContextLoader.AttachAsync(new[] { Ctx("AAA"), Ctx("BBB") }, bars, SignalToOrderLag.Refinement15m,
            new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), NullLogger.Instance);

        Assert.Equal(3, result[0].HourlyBars!.Count);
        Assert.Equal(2, result[0].QuarterHourBars!.Count);
        Assert.Null(result[1].HourlyBars);                       // BBB has nothing cached: left null, not an empty list pretending to be data
        Assert.Equal(1, coverage!.SymbolsWithHourly);
        Assert.Equal(2, coverage.SymbolsTotal);
        Assert.Equal(d1, coverage.FirstHourlySession);
        Assert.Equal(d2, coverage.LastHourlySession);
        Assert.Equal(1, coverage.SymbolsWithQuarterHour);
        Assert.Equal(result[0].Bars.Count, Ctx("AAA").Bars.Count);   // the daily series is carried over untouched
    }

    [Fact]
    public async Task Refinement60m_DoesNotLoad15mBars()
    {
        using var host = CliTestHost.Create();
        var bars = host.Repo<IHistoricalBarRepository>();
        await bars.UpsertRangeAsync(new[] { Bar("AAA", new DateOnly(2026, 2, 10), 9, 15, Timeframe.H1), Bar("AAA", new DateOnly(2026, 2, 10), 9, 15, Timeframe.M15) });

        var (result, coverage) = await IntradayContextLoader.AttachAsync(new[] { Ctx("AAA") }, bars, SignalToOrderLag.Refinement60m,
            new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), NullLogger.Instance);

        Assert.NotNull(result[0].HourlyBars);
        Assert.Null(result[0].QuarterHourBars);
        Assert.Equal(0, coverage!.SymbolsWithQuarterHour);
    }
}
