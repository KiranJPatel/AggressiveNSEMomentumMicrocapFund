using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class YahooDownloadAsyncTests
{
    private static string TwoDays(DateOnly a, DateOnly b) =>
        "{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"T.NS\"},\"timestamp\":[" + Ts(a) + "," + Ts(b) + "],"
        + "\"indicators\":{\"quote\":[{\"open\":[100,102],\"high\":[105,107],\"low\":[95,97],\"close\":[101,103],\"volume\":[10000,12000]}],"
        + "\"adjclose\":[{\"adjclose\":[101,103]}]}}],\"error\":null}}";
    private static long Ts(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();

    private static (YahooFinanceHistoricalDataProvider Provider, SqliteHistoricalBarRepository Repo, SqliteConnection Db) Make(FakeYahooHttpFetcher fetcher)
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_dl_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        var repo = new SqliteHistoricalBarRepository(db);
        return (new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance), repo, db);
    }

    private static readonly DateOnly Wed = new(2026, 10, 7), Thu = new(2026, 10, 8);

    [Fact]
    public async Task DownloadAsync_PersistsBars_AndReportsRange()
    {
        var (p, repo, _) = Make(new FakeYahooHttpFetcher((s, f, t) => TwoDays(Wed, Thu)));
        var r = await p.DownloadAsync("TEST", Wed, Thu);
        Assert.True(r.Success); Assert.Equal(2, r.BarsFetched); Assert.Equal(2, r.BarsPersisted);
        Assert.Equal(Wed, r.FirstDate); Assert.Equal(Thu, r.LastDate);
        Assert.Equal(2, (await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily)).Count);
    }

    [Fact]
    public async Task DownloadAsync_ExcludeFrom_FetchesButDoesNotStoreTheProvisionalDay()
    {
        var (p, repo, _) = Make(new FakeYahooHttpFetcher((s, f, t) => TwoDays(Wed, Thu)));
        var r = await p.DownloadAsync("TEST", Wed, Thu, excludeFrom: Thu);
        Assert.True(r.Success); Assert.Equal(2, r.BarsFetched); Assert.Equal(1, r.BarsPersisted); Assert.Equal(Wed, r.LastDate);
        var stored = await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily);
        Assert.Equal(1, stored.Count);
        Assert.Equal(Wed, DateOnly.FromDateTime(stored[0].Timestamp.DateTime));
    }

    [Fact]
    public async Task DownloadAsync_RefetchOverwritesAPreviouslyStoredBar()
    {
        var first = true;
        var (p, repo, _) = Make(new FakeYahooHttpFetcher((s, f, t) => { var j = first ? TwoDays(Wed, Thu) : TwoDays(Wed, Thu).Replace("\"close\":[101,103]", "\"close\":[101,110]"); first = false; return j; }));
        await p.DownloadAsync("TEST", Wed, Thu);
        await p.DownloadAsync("TEST", Wed, Thu);
        var thu = (await repo.GetRangeAsync("TEST", Thu, Thu, Timeframe.Daily)).Single();
        Assert.Equal(110m, thu.Close);                                  // the revised value replaced the old one; no duplicate row
        Assert.Equal(2, (await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily)).Count);
    }

    [Fact]
    public async Task DownloadAsync_HttpFailure_IsReported_NotSwallowed_AndStoresNothing()
    {
        var (p, repo, _) = Make(new FakeYahooHttpFetcher((s, f, t) => throw new YahooHttpException(s, 403, "Forbidden")));
        var r = await p.DownloadAsync("TEST", Wed, Thu);
        Assert.False(r.Success); Assert.Equal("HTTP 403", r.Error);
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily));
    }

    [Fact]
    public async Task DownloadAsync_GarbageResponse_IsAFailure()
    {
        var (p, _, _) = Make(new FakeYahooHttpFetcher((s, f, t) => "<html>not json</html>"));
        var r = await p.DownloadAsync("TEST", Wed, Thu);
        Assert.False(r.Success); Assert.True(r.Error!.Contains("unparseable"));
    }

    [Fact]
    public async Task DownloadAsync_BackwardsRange_IsRejectedWithoutARequest()
    {
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => TwoDays(Wed, Thu));
        var (p, _, _) = Make(fetcher);
        var r = await p.DownloadAsync("TEST", Thu, Wed);
        Assert.False(r.Success); Assert.Empty(fetcher.Calls);
    }

    [Fact]
    public async Task DownloadAsync_AppliesTheNsSuffix_ButNotToIndices()
    {
        var fetcher = new FakeYahooHttpFetcher((s, f, t) => TwoDays(Wed, Thu));
        var (p, _, _) = Make(fetcher);
        await p.DownloadAsync("RELIANCE", Wed, Thu);
        await p.DownloadAsync("^CRSLDX", Wed, Thu);
        Assert.Equal("RELIANCE.NS", fetcher.Calls[0].Symbol);
        Assert.Equal("^CRSLDX", fetcher.Calls[1].Symbol);
    }
}
