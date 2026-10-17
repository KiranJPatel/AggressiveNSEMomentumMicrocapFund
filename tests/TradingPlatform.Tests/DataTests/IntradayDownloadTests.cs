using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.CliTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>Fake that serves both the daily and the interval entry points and records what the provider asked for.</summary>
public sealed class FakeIntervalFetcher : IYahooHttpFetcher, IYahooIntervalFetcher
{
    private readonly Func<DateOnly, DateOnly, string, string> _responder;
    public List<(DateOnly From, DateOnly To, string Interval)> Calls { get; } = new();
    public FakeIntervalFetcher(Func<DateOnly, DateOnly, string, string> responder) => _responder = responder;

    public Task<string> FetchIntervalRawJsonAsync(string symbol, DateOnly from, DateOnly to, string interval, CancellationToken ct = default)
    {
        Calls.Add((from, to, interval));
        return Task.FromResult(_responder(from, to, interval));
    }

    public Task<string> FetchRawJsonAsync(string symbol, DateOnly from, DateOnly to, CancellationToken ct = default) => throw new InvalidOperationException("daily endpoint must not be used for intraday");
}

public class IntradayDownloadTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateOnly Wed = new(2026, 10, 7), Thu = new(2026, 10, 8), Fri = new(2026, 10, 9);

    private static long Epoch(DateOnly d, int h, int m) => new DateTimeOffset(d.ToDateTime(new TimeOnly(h, m)), Ist).ToUnixTimeSeconds();

    /// <summary>Seven hourly candles (09:15..15:15) for each given date.</summary>
    private static string Hourly(IEnumerable<DateOnly> dates, long volume = 1000)
    {
        var stamps = new List<long>(); var o = new List<string>(); var h = new List<string>(); var l = new List<string>(); var c = new List<string>(); var v = new List<string>();
        foreach (var d in dates)
            for (var i = 0; i < 7; i++)
            {
                stamps.Add(Epoch(d, 9 + i, 15)); o.Add("100"); h.Add("102"); l.Add("99"); c.Add("101"); v.Add(volume.ToString());
            }
        return "{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"T.NS\"},\"timestamp\":[" + string.Join(",", stamps) + "],\"indicators\":{\"quote\":[{\"open\":[" + string.Join(",", o)
            + "],\"high\":[" + string.Join(",", h) + "],\"low\":[" + string.Join(",", l) + "],\"close\":[" + string.Join(",", c) + "],\"volume\":[" + string.Join(",", v) + "]}]}}],\"error\":null}}";
    }

    private static (YahooFinanceHistoricalDataProvider P, SqliteHistoricalBarRepository R) Make(FakeIntervalFetcher f)
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_intra_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        var repo = new SqliteHistoricalBarRepository(db);
        return (new YahooFinanceHistoricalDataProvider(f, repo, new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance), repo);
    }

    // ---------------- parser ----------------

    [Fact]
    public void Parser_KeepsEachIntradayCandleAsItsOwnIstTimestamp_ButStillCollapsesDailyToMidnight()
    {
        var json = Hourly(new[] { Wed });

        var hourly = YahooChartResponseParser.Parse(json, "T", "yahoo", Timeframe.H1).Bars;
        var daily = YahooChartResponseParser.Parse(json, "T", "yahoo", Timeframe.Daily).Bars;

        Assert.Equal(7, hourly.Select(b => b.Timestamp).Distinct().Count());
        Assert.Equal(new TimeSpan(9, 15, 0), hourly[0].Timestamp.TimeOfDay);
        Assert.Equal(new TimeSpan(15, 15, 0), hourly[6].Timestamp.TimeOfDay);
        Assert.Equal(Ist, hourly[0].Timestamp.Offset);
        Assert.Equal(1, daily.Select(b => b.Timestamp).Distinct().Count());   // the legacy daily behaviour is unchanged
    }

    // ---------------- provider ----------------

    [Fact]
    public async Task Hourly_RequestsTheIntervalToken_StoresDistinctCandles_AndTheSecondRunFetchesNothing()
    {
        var fetcher = new FakeIntervalFetcher((f, t, i) => Hourly(new[] { Wed, Thu }));
        var (p, repo) = Make(fetcher);

        var first = await p.DownloadIntradayAsync("TEST", Wed, Thu, Timeframe.H1);
        var second = await p.DownloadIntradayAsync("TEST", Wed, Thu, Timeframe.H1);

        Assert.True(first.Success);
        Assert.Equal(14, first.BarsPersisted);
        Assert.Equal("60m", fetcher.Calls[0].Interval);
        Assert.Equal(14, (await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.H1)).Count);
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Thu, Timeframe.Daily));    // never leaks into the daily series
        Assert.True(second.Success && second.BarsPersisted == 0);
        Assert.Equal(1, fetcher.Calls.Count);                                          // cached days are not re-requested
    }

    [Fact]
    public async Task OnlyTheMissingDaysAreFetched()
    {
        var fetcher = new FakeIntervalFetcher((f, t, i) => Hourly(Enumerable.Range(0, t.DayNumber - f.DayNumber + 1).Select(n => f.AddDays(n)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))));
        var (p, _) = Make(fetcher);
        await p.DownloadIntradayAsync("TEST", Wed, Wed, Timeframe.H1);

        await p.DownloadIntradayAsync("TEST", Wed, Fri, Timeframe.H1);

        Assert.Equal(2, fetcher.Calls.Count);
        Assert.Equal((Thu, Fri), (fetcher.Calls[1].From, fetcher.Calls[1].To));        // Wednesday was already cached
    }

    [Fact]
    public async Task RangesOlderThanYahoosRetention_AreClamped()
    {
        var fetcher = new FakeIntervalFetcher((f, t, i) => Hourly(Array.Empty<DateOnly>()));
        var (p, _) = Make(fetcher);

        await p.DownloadIntradayAsync("TEST", Wed.AddDays(-400), Wed, Timeframe.M15);

        Assert.True(fetcher.Calls.All(c => c.From >= Wed.AddDays(-59)), "15m history beyond 59 days must not be requested");
        Assert.Equal("15m", fetcher.Calls[0].Interval);
    }

    [Fact]
    public async Task DailyTimeframe_FetcherWithoutIntervalSupport_AndReversedRange_AreRejectedCleanly()
    {
        var (p, _) = Make(new FakeIntervalFetcher((f, t, i) => Hourly(Array.Empty<DateOnly>())));
        Assert.False((await p.DownloadIntradayAsync("TEST", Wed, Thu, Timeframe.Daily)).Success);
        Assert.False((await p.DownloadIntradayAsync("TEST", Thu, Wed, Timeframe.H1)).Success);

        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_intra_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        var dailyOnly = new YahooFinanceHistoricalDataProvider(new FakeYahooHttpFetcher((s, f, t) => ""), new SqliteHistoricalBarRepository(db), new SqliteSystemEventWriter(db), new YahooFinanceOptions(), NullLogger<YahooFinanceHistoricalDataProvider>.Instance);
        var r = await dailyOnly.DownloadIntradayAsync("TEST", Wed, Thu, Timeframe.H1);
        Assert.False(r.Success);
        Assert.True(r.Error!.Contains("intraday"));
    }

    [Fact]
    public async Task ZeroVolumeCandles_AreStored_ButNotReportedAsOneIssueEach()
    {
        var (p, repo) = Make(new FakeIntervalFetcher((f, t, i) => Hourly(new[] { Wed }, volume: 0)));

        var r = await p.DownloadIntradayAsync("TEST", Wed, Wed, Timeframe.H1);

        Assert.Equal(7, r.BarsPersisted);
        Assert.Equal(0, r.QualityIssues);
        Assert.Equal(7, (await repo.GetRangeAsync("TEST", Wed, Wed, Timeframe.H1)).Count);
    }

    [Fact]
    public async Task TheStillFormingSession_IsNotStored()
    {
        var (p, repo) = Make(new FakeIntervalFetcher((f, t, i) => Hourly(new[] { Wed, Thu })));

        var r = await p.DownloadIntradayAsync("TEST", Wed, Thu, Timeframe.H1, excludeFrom: Thu);

        Assert.Equal(7, r.BarsPersisted);
        Assert.Empty(await repo.GetRangeAsync("TEST", Thu, Thu, Timeframe.H1));
    }

    [Fact]
    public async Task FetchFailure_LeavesTheRangeMissing_AndALaterRunRecovers()
    {
        var up = false;
        var (p, repo) = Make(new FakeIntervalFetcher((f, t, i) => up ? Hourly(new[] { Wed }) : throw new HttpRequestException("connection reset")));

        Assert.False((await p.DownloadIntradayAsync("TEST", Wed, Wed, Timeframe.H1)).Success);
        Assert.Empty(await repo.GetRangeAsync("TEST", Wed, Wed, Timeframe.H1));

        up = true;
        Assert.Equal(7, (await p.DownloadIntradayAsync("TEST", Wed, Wed, Timeframe.H1)).BarsPersisted);
    }

    // ---------------- command ----------------

    private sealed class StubDownloader : IIntradayBarDownloader
    {
        public Func<string, BarDownloadResult>? Respond { get; set; }
        public List<(string Symbol, DateOnly From, DateOnly To, Timeframe Tf, DateOnly? Exclude)> Calls { get; } = new();
        public int MaxHistoryDays(Timeframe tf) => YahooIntradayLimits.MaxHistoryDays(tf);
        public Task<BarDownloadResult> DownloadIntradayAsync(string symbol, DateOnly from, DateOnly to, Timeframe tf, DateOnly? excludeFrom = null, CancellationToken ct = default)
        {
            Calls.Add((symbol, from, to, tf, excludeFrom));
            return Task.FromResult(Respond?.Invoke(symbol) ?? new BarDownloadResult(symbol, true, 7, 7, from, to, null, 0));
        }
    }

    [Fact]
    public void ParseArgs_RequiresAValidTimeframe_AndBoundsDaysPerTimeframe()
    {
        var d = new StubDownloader();
        var today = new DateOnly(2026, 10, 13);

        Assert.True(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday" }, today, d).Error!.Contains("required"));
        Assert.True(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe", "2m" }, today, d).Error!.Contains("60m, 15m or 5m"));
        Assert.NotNull(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe", "15m", "--days", "100" }, today, d).Error);   // 15m retention is 59 days
        Assert.Null(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe", "60m", "--days", "700" }, today, d).Error);       // 60m allows 729
        Assert.NotNull(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe", "60m", "--to", "13/10/2026" }, today, d).Error);
        Assert.NotNull(IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe" }, today, d).Error);

        var ok = IntradayDownloadCommand.ParseArgs(new[] { "download-intraday", "--timeframe", "1h", "--symbols", "A, B" }, today, d).Settings!;
        Assert.Equal(Timeframe.H1, ok.Timeframe);
        Assert.Equal(729, ok.Days);
        Assert.True(ok.OnlySymbols!.SequenceEqual(new[] { "A", "B" }));
    }

    [Fact]
    public async Task Service_PassesTheProvisionalCutoff_ClampsDays_AndAbortsAfterConsecutiveFailures()
    {
        using var host = CliTestHost.Create();
        var instruments = host.Repo<IInstrumentRepository>();
        foreach (var sym in new[] { "AAA", "BBB", "CCC", "DDD" })
            await instruments.UpsertAsync(new Instrument { Symbol = sym, NseSymbol = sym, YahooSymbol = sym + ".NS", CompanyName = sym, Exchange = "NSE", Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var stub = new StubDownloader { Respond = s => BarDownloadResult.Failed(s, "HTTP 429") };
        var scanner = host.Options<ScannerOptions>().Value;
        // Tuesday 13 Oct 2026 11:00 IST (05:30 UTC): the session is still forming, so today is the provisional cutoff.
        var service = new IntradayDownloadService(instruments, stub, scanner, CliTestHost.Logger, () => new DateTimeOffset(2026, 10, 13, 5, 30, 0, TimeSpan.Zero));

        var summary = await service.RunAsync(new IntradayDownloadSettings(Timeframe.M15, new DateOnly(2026, 10, 13), Days: 500, IncludeToday: false, MaxConsecutiveFailures: 2, OnlySymbols: null, SettlementMinutes: 20), null, default);

        Assert.True(summary.Aborted);
        Assert.Equal(2, stub.Calls.Count);                                   // stopped after 2 consecutive failures, not all 4 symbols
        Assert.Equal(new DateOnly(2026, 10, 13), stub.Calls[0].Exclude);     // today's forming session must not be stored
        Assert.Equal(new DateOnly(2026, 10, 12), stub.Calls[0].To);          // last COMPLETED weekday
        Assert.Equal(new DateOnly(2026, 10, 12).AddDays(-59), stub.Calls[0].From);   // --days 500 clamped to 15m's retention
    }
}
