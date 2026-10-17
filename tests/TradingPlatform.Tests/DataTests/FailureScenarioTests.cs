using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>Spec §7.4 failure tests for the data layer: network disconnect, timeout, 429, user cancellation, SQLite locked — each must fail safely (reported, nothing half-written, recoverable).</summary>
public class FailureScenarioTests
{
    private static readonly DateOnly Wed = new(2026, 10, 7), Thu = new(2026, 10, 8);
    private static long Ts(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();

    private static string TwoDays() =>
        "{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"T.NS\"},\"timestamp\":[" + Ts(Wed) + "," + Ts(Thu) + "],"
        + "\"indicators\":{\"quote\":[{\"open\":[100,102],\"high\":[105,107],\"low\":[95,97],\"close\":[101,103],\"volume\":[10000,12000]}],"
        + "\"adjclose\":[{\"adjclose\":[101,103]}]}}],\"error\":null}}";

    private static (YahooFinanceHistoricalDataProvider Provider, SqliteHistoricalBarRepository Repo) Make(FakeYahooHttpFetcher fetcher)
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_fail_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        var repo = new SqliteHistoricalBarRepository(db);
        return (new YahooFinanceHistoricalDataProvider(fetcher, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance), repo);
    }

    [Fact]
    public async Task NetworkDisconnect_IsReportedAsAFailure_StoresNothing_AndALaterRetryRecovers()
    {
        var online = false;
        var (p, repo) = Make(new FakeYahooHttpFetcher((s, f, t) =>
            online ? TwoDays() : throw new HttpRequestException("connection reset", new SocketException(10054))));

        var down = await p.DownloadAsync("TEST", Wed, Thu);
        Assert.False(down.Success);
        Assert.True(down.Error!.Contains("HttpRequestException"));
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily));

        online = true; // network is back: the range was left "missing", so the next call fetches it
        var up = await p.DownloadAsync("TEST", Wed, Thu);
        Assert.True(up.Success);
        Assert.Equal(2, (await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily)).Count);
    }

    [Fact]
    public async Task Timeout_NotRequestedByTheUser_IsAFailure_NotACrash()
    {
        var (p, repo) = Make(new FakeYahooHttpFetcher((s, f, t) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));

        var r = await p.DownloadAsync("TEST", Wed, Thu); // token NOT cancelled: this is a timeout, not Ctrl+C

        Assert.False(r.Success);
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily));
    }

    [Fact]
    public async Task UserCancellation_Propagates_InsteadOfBeingReportedAsADownloadFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (p, _) = Make(new FakeYahooHttpFetcher((s, f, t) => throw new OperationCanceledException(cts.Token)));

        var threw = false;
        try { await p.DownloadAsync("TEST", Wed, Thu, cancellationToken: cts.Token); }
        catch (OperationCanceledException) { threw = true; }

        Assert.True(threw, "Ctrl+C must stop the download run, not be counted as one more failed symbol");
    }

    [Fact]
    public async Task RateLimited429_IsReportedWithItsStatusCode_AndStoresNothing()
    {
        var (p, repo) = Make(new FakeYahooHttpFetcher((s, f, t) => throw new YahooHttpException(s, 429, "Too Many Requests")));

        var r = await p.DownloadAsync("TEST", Wed, Thu);

        Assert.False(r.Success);
        Assert.Equal("HTTP 429", r.Error);
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily));
    }

    [Fact]
    public void SqliteLocked_ASecondWriterFailsLoudly_ReadersStillWork_AndTheWriteSucceedsOnceTheLockClears()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tp_lock_{Guid.NewGuid():N}.db");
        using var holder = new SqliteConnection(path);
        using var other = new SqliteConnection(path);
        holder.Execute("CREATE TABLE t (x INTEGER);");
        holder.Execute("INSERT INTO t VALUES (1);");

        holder.Execute("BEGIN IMMEDIATE;");                 // holder now owns the write lock
        holder.Execute("INSERT INTO t VALUES (2);");

        // WAL: a reader is not blocked by the open write transaction, and sees only committed data.
        var seenDuringLock = other.Query("SELECT COUNT(*) AS c FROM t;", r => r.GetInt64("c")).Single();
        Assert.Equal(1L, seenDuringLock);

        // A second writer waits out the 5 s busy timeout and then fails with a SqliteException — it must not hang or corrupt anything.
        Assert.Throws<SqliteException>(() => other.Execute("INSERT INTO t VALUES (3);"));

        holder.Execute("COMMIT;");
        other.Execute("INSERT INTO t VALUES (3);");         // lock cleared: the retry succeeds

        var rows = other.Query("SELECT x FROM t ORDER BY x;", r => r.GetInt64("x"));
        Assert.True(rows.SequenceEqual(new long[] { 1, 2, 3 }), "committed rows survive; the failed write left nothing behind");
    }
}
