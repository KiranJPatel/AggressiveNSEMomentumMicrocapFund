using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>
/// End-to-end tests of the orchestration in YahooFinanceHistoricalDataProvider
/// (cache check -> missing-range calc -> fetch -> validate -> persist),
/// using a real temp SQLite database (proven reliable by RepositoryTests)
/// and a fake HTTP fetcher (no real network — see docs/gap-log.md).
/// </summary>
public class YahooFinanceHistoricalDataProviderTests
{
    private static string ValidDayFixture(DateOnly date, decimal close, long volume = 10000) =>
        $$"""
        {
          "chart": {
            "result": [
              {
                "meta": { "symbol": "TEST.NS" },
                "timestamp": [{{new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUnixTimeSeconds()}}],
                "indicators": {
                  "quote": [ { "open": [{{close}}], "high": [{{close + 5}}], "low": [{{close - 5}}], "close": [{{close}}], "volume": [{{volume}}] } ],
                  "adjclose": [ { "adjclose": [{{close}}] } ]
                }
              }
            ],
            "error": null
          }
        }
        """;

    private static SqliteConnection OpenInitializedTempDb()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_yahoo_test_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        return db;
    }

    [Fact]
    public async Task NonDailyTimeframe_ThrowsNotSupported()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => throw new InvalidOperationException("should not be called"));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        await AssertThrowsAsync<NotSupportedException>(() => provider.GetBarsAsync("TEST", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5), Timeframe.Weekly));
    }

    [Fact]
    public async Task FullyMissingRange_FetchesOnce_AndPersists()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => ValidDayFixture(monday, 100m));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("TEST", monday, monday, Timeframe.Daily);

        Assert.Equal(1, fetcher.Calls.Count);
        Assert.Equal(1, bars.Count);
        Assert.Equal(100m, bars[0].Close);

        // Persisted for real — a fresh repository read confirms it, not just the in-memory return value.
        var reread = await repo.GetRangeAsync("TEST", monday, monday, Timeframe.Daily);
        Assert.Equal(1, reread.Count);
    }

    [Fact]
    public async Task IndexTickerStartingWithCaret_IsNeverGivenAnExchangeSuffix()
    {
        // Tranche 18: BenchmarkOptions.BroadIndexSymbol defaults to
        // "^CRSLDX" (Nifty 500's real Yahoo ticker — Yahoo's own "^"
        // convention for indices, which never takes ".NS"). Appending the
        // suffix here would silently request a symbol Yahoo doesn't serve.
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => ValidDayFixture(monday, 100m));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        await provider.GetBarsAsync("^CRSLDX", monday, monday, Timeframe.Daily);

        Assert.Equal(1, fetcher.Calls.Count);
        Assert.Equal("^CRSLDX", fetcher.Calls[0].Symbol);
    }

    [Fact]
    public async Task SymbolAlreadyEndingInSuffix_IsNotDoubleSuffixed()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => ValidDayFixture(monday, 100m));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        await provider.GetBarsAsync("NMIDCAP150.NS", monday, monday, Timeframe.Daily);

        Assert.Equal("NMIDCAP150.NS", fetcher.Calls[0].Symbol);
    }

    [Fact]
    public async Task AlreadyCachedDate_DoesNotCallFetcherAgain()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);

        await repo.UpsertRangeAsync(new[]
        {
            new HistoricalBar { Symbol = "TEST", Timestamp = new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = 100, High = 105, Low = 99, Close = 103, AdjustedClose = 103, Volume = 5000, Source = "Yahoo", Timeframe = Timeframe.Daily }
        });

        var fetcher = new FakeYahooHttpFetcher((s, f, t) => throw new InvalidOperationException("should not be called — date is already cached"));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        var bars = await provider.GetBarsAsync("TEST", monday, monday, Timeframe.Daily);

        Assert.Equal(0, fetcher.Calls.Count);
        Assert.Equal(1, bars.Count);
    }

    [Fact]
    public async Task FetchFailure_LeavesRangeMissing_DoesNotThrow_ReturnsWhateverIsCached()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => throw new YahooHttpException(s, 403, "forbidden"));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        // Must not throw — the whole point of "leave the range missing" is a graceful degrade.
        var bars = await provider.GetBarsAsync("TEST", monday, monday, Timeframe.Daily);

        Assert.Equal(0, bars.Count);
    }

    [Fact]
    public async Task DataQualityIssue_IsPersistedToSystemEvents()
    {
        using var db = OpenInitializedTempDb();
        var repo = new SqliteHistoricalBarRepository(db);
        var monday = new DateOnly(2026, 1, 5);
        // Zero volume triggers an Info-level DataQuality flag from HistoricalBarValidator.
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => ValidDayFixture(monday, 100m, volume: 0));
        var provider = new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);

        await provider.GetBarsAsync("TEST", monday, monday, Timeframe.Daily);

        var events = db.Query("SELECT Category, Message FROM SystemEvents WHERE Category = 'DataQuality';", row => row.GetString("Message"));
        Assert.Equal(1, events.Count);
        Assert.True(events[0].Contains("ZeroVolume"));
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new AssertionFailedException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
