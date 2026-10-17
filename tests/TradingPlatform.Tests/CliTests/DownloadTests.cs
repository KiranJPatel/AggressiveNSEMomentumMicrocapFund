using Microsoft.Extensions.Logging.Abstractions;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class DownloadTests
{
    private static readonly ScannerOptions Scanner = new(); // 09:15-15:30 Asia/Kolkata
    private static DateTimeOffset Ist(int y, int m, int d, int h, int min) => new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
    // 2026-10-08 is a Thursday.
    private static readonly DateOnly Thu = new(2026, 10, 8);

    private static DownloadSettings Settings(DateOnly? to = null, bool force = false, DateOnly? from = null, bool keepTurnover = false, int maxFail = 10, IReadOnlyList<string>? only = null, bool includeToday = false) =>
        new(to ?? Thu, 5, 5, force, from, includeToday, keepTurnover, maxFail, only, true, 20);

    // ---- pure planning ----
    [Fact]
    public void ProvisionalCutoff_TodayWhileSessionOpenOrUnsettled_NullOnceSettledOnWeekendsOrOverridden()
    {
        Assert.Equal(Thu, DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 8, 11, 0), Scanner, 20, false));   // mid-session
        Assert.Equal(Thu, DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 8, 8, 0), Scanner, 20, false));    // pre-open: today has no bar yet but must not be stored if one appears
        Assert.Equal(Thu, DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 8, 15, 40), Scanner, 20, false));  // closed but inside the 20-min settlement window
        Assert.Null(DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 8, 15, 55), Scanner, 20, false));        // settled
        Assert.Null(DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 10, 12, 0), Scanner, 20, false));        // Saturday
        Assert.Null(DownloadPlanner.ProvisionalCutoff(Ist(2026, 10, 8, 11, 0), Scanner, 20, true));          // --include-today
    }

    [Fact]
    public void LastExpectedBarDate_SkipsWeekends_AndTheProvisionalDay()
    {
        Assert.Equal(new DateOnly(2026, 10, 7), DownloadPlanner.LastExpectedBarDate(Thu, Thu));                       // Thursday provisional -> Wednesday
        Assert.Equal(Thu, DownloadPlanner.LastExpectedBarDate(Thu, null));
        Assert.Equal(new DateOnly(2026, 10, 9), DownloadPlanner.LastExpectedBarDate(new DateOnly(2026, 10, 11), null)); // Sunday -> Friday
        Assert.Equal(new DateOnly(2026, 10, 9), DownloadPlanner.LastExpectedBarDate(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12))); // Monday provisional -> Friday
    }

    [Fact]
    public void Plan_CoversNoCache_UpToDate_Behind_Force_AndBackfill()
    {
        var last = new DateOnly(2026, 10, 7);
        var cov = new BarCoverage("A", new DateOnly(2021, 10, 8), last, 1200);
        var none = DownloadPlanner.Plan("A", null, Thu, last, Settings());
        Assert.True(none.Action == DownloadAction.Download && none.From == Thu.AddYears(-5));

        Assert.Equal(DownloadAction.UpToDate, DownloadPlanner.Plan("A", cov, Thu, last, Settings()).Action);

        var behind = DownloadPlanner.Plan("A", cov with { LastDate = new DateOnly(2026, 10, 1) }, Thu, last, Settings());
        Assert.True(behind.Action == DownloadAction.Download && behind.From == new DateOnly(2026, 9, 26));            // lastCached - 5d overlap

        var force = DownloadPlanner.Plan("A", cov, Thu, last, Settings(force: true));
        Assert.True(force.Action == DownloadAction.Download && force.From == last.AddDays(-5));

        var back = DownloadPlanner.Plan("A", cov, Thu, last, Settings(from: new DateOnly(2018, 1, 1)));
        Assert.True(back.Action == DownloadAction.Download && back.From == new DateOnly(2018, 1, 1) && back.Reason.Contains("back-filling"));
    }

    [Fact]
    public void ParseArgs_ValidatesEveryOption()
    {
        var today = Thu;
        Assert.NotNull(DownloadCommand.ParseArgs(new[] { "download" }, today).Settings);
        var s = DownloadCommand.ParseArgs(new[] { "download", "--symbols", "A, B", "--years", "3", "--refresh-days", "2", "--force", "--no-benchmarks", "--include-today", "--keep-turnover" }, today).Settings!;
        Assert.True(s.OnlySymbols!.SequenceEqual(new[] { "A", "B" }) && s.HistoryYears == 3 && s.RefreshDays == 2 && s.Force && !s.IncludeBenchmarks && s.IncludeToday && s.KeepTurnover);
        foreach (var bad in new[] { new[] { "--years", "0" }, new[] { "--years", "abc" }, new[] { "--from", "2026-13-01" }, new[] { "--to", "yesterday" }, new[] { "--refresh-days", "-1" }, new[] { "--max-failures", "0" }, new[] { "--years" } })
            Assert.NotNull(DownloadCommand.ParseArgs(new[] { "download" }.Concat(bad).ToArray(), today).Error);
        Assert.NotNull(DownloadCommand.ParseArgs(new[] { "download", "--from", "2026-12-01", "--to", "2026-10-01" }, today).Error);
    }

    // ---- the service ----
    private sealed class FakeDownloader : IBarDownloader
    {
        private readonly IHistoricalBarRepository _bars;
        public readonly List<(string Symbol, DateOnly From, DateOnly To, DateOnly? ExcludeFrom)> Calls = new();
        public readonly HashSet<string> Fail = new(); public readonly HashSet<DateOnly> Holidays = new(); public bool FailEverything;
        public FakeDownloader(IHistoricalBarRepository bars) => _bars = bars;

        public async Task<BarDownloadResult> DownloadAsync(string symbol, DateOnly from, DateOnly to, DateOnly? excludeFrom = null, CancellationToken ct = default)
        {
            Calls.Add((symbol, from, to, excludeFrom));
            if (FailEverything || Fail.Contains(symbol)) return BarDownloadResult.Failed(symbol, "HTTP 403");
            var list = new List<HistoricalBar>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (excludeFrom is { } cut && d >= cut) continue;
                if (Holidays.Contains(d)) continue;                      // market closed: the source simply has no bar
                list.Add(new HistoricalBar { Symbol = symbol, Timestamp = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)), Open = 100, High = 101, Low = 99, Close = 100, AdjustedClose = 100, Volume = 1_000_000, Source = "fake", Timeframe = Timeframe.Daily });
            }
            if (list.Count > 0) await _bars.UpsertRangeAsync(list, ct);
            return list.Count == 0 ? new BarDownloadResult(symbol, true, 0, 0, null, null, null, 0) : new BarDownloadResult(symbol, true, list.Count, list.Count, DateOnly.FromDateTime(list[0].Timestamp.DateTime), DateOnly.FromDateTime(list[^1].Timestamp.DateTime), null, 0);
        }
    }

    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public IInstrumentRepository Instruments => Host.Repo<IInstrumentRepository>();
        public IHistoricalBarRepository Bars => Host.Repo<IHistoricalBarRepository>();
        public readonly FakeDownloader Downloader;
        public DateTimeOffset Now = Ist(2026, 10, 8, 17, 0);   // Thursday evening, settled
        public Ctx() => Downloader = new FakeDownloader(Bars);
        public DownloadService Service() => new(Instruments, Bars, Downloader, Host.Repo<IEngineStateRepository>(), Host.Options<BenchmarkOptions>().Value, Scanner, NullLogger.Instance, () => Now);
        public async Task AddAsync(params string[] symbols) { foreach (var s in symbols) await Instruments.UpsertAsync(new Instrument { Symbol = s, NseSymbol = s, YahooSymbol = s + ".NS", CompanyName = s, Exchange = "NSE", Active = true }); }
        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public async Task FirstRun_DownloadsBenchmarksFirst_ThenUniverse_AndDerivesTurnoverFromTheBars()
    {
        using var c = new Ctx(); await c.AddAsync("AAA", "BBB");
        var summary = await c.Service().RunAsync(Settings(), null, default);

        var bench = c.Host.Options<BenchmarkOptions>().Value;
        Assert.Equal(bench.BroadIndexSymbol, c.Downloader.Calls[0].Symbol);
        Assert.True(c.Downloader.Calls.Select(x => x.Symbol).Skip(1).Take(2).Contains("AAA") || c.Downloader.Calls.Count >= 3);
        Assert.Equal(0, summary.Failed); Assert.False(summary.Aborted);
        Assert.True(summary.Downloaded >= 3 && summary.BarsPersisted > 1000);
        Assert.Equal(100m * 1_000_000m, (await c.Instruments.GetBySymbolAsync("AAA"))!.AverageDailyTurnover);       // derived: close x volume
        Assert.Equal(1, (await c.Bars.GetCoverageAsync(Timeframe.Daily)).Count(x => x.Symbol == "AAA"));
    }

    [Fact]
    public async Task SecondRun_IsUpToDate_AndMakesNoRequests()
    {
        using var c = new Ctx(); await c.AddAsync("AAA");
        await c.Service().RunAsync(Settings(), null, default);
        c.Downloader.Calls.Clear();
        var again = await c.Service().RunAsync(Settings(), null, default);
        Assert.Empty(c.Downloader.Calls);
        Assert.True(again.UpToDate >= 2 && again.Downloaded == 0 && again.Failed == 0);
    }

    [Fact]
    public async Task NextDay_FetchesOnlyTheGap_WithTheRefreshOverlap()
    {
        using var c = new Ctx(); await c.AddAsync("AAA");
        await c.Service().RunAsync(Settings(), null, default);                       // cached through Thu 10-08 (settled)
        c.Now = Ist(2026, 10, 12, 17, 0);                                              // Monday evening
        c.Downloader.Calls.Clear();
        await c.Service().RunAsync(Settings(to: new DateOnly(2026, 10, 12)), null, default);
        var call = c.Downloader.Calls.First(x => x.Symbol == "AAA");
        Assert.Equal(new DateOnly(2026, 10, 3), call.From);      // last cached 10-08 minus 5 days
        Assert.Equal(new DateOnly(2026, 10, 12), call.To);
    }

    [Fact]
    public async Task OneFailingSymbol_IsReported_AndTheRestStillDownload_AndSummaryIsPersisted()
    {
        using var c = new Ctx(); await c.AddAsync("AAA", "BAD", "CCC");
        c.Downloader.Fail.Add("BAD");
        var s = await c.Service().RunAsync(Settings(), null, default);
        Assert.Equal(1, s.Failed); Assert.Equal("BAD", s.Failures[0].Symbol); Assert.Equal("HTTP 403", s.Failures[0].Error);
        Assert.True(c.Downloader.Calls.Any(x => x.Symbol == "CCC"));                  // processing continued past the failure
        Assert.False(s.Aborted);
        var saved = await c.Host.Repo<IEngineStateRepository>().GetAsync(DownloadService.LastDownloadStateKey);
        Assert.True(saved!.Contains("BAD"));
    }

    [Fact]
    public async Task ConsecutiveFailures_AbortTheRun_InsteadOfRetryingEverySymbol()
    {
        using var c = new Ctx(); await c.AddAsync("A1", "A2", "A3", "A4", "A5", "A6");
        c.Downloader.FailEverything = true;
        var s = await c.Service().RunAsync(Settings(maxFail: 3), null, default);
        Assert.True(s.Aborted); Assert.True(s.AbortReason!.Contains("3 consecutive failures"));
        Assert.Equal(3, c.Downloader.Calls.Count);
    }

    [Fact]
    public async Task WhileTheSessionIsForming_TodaysBarIsExcluded_AfterSettlement_ItIsNot()
    {
        using var c = new Ctx(); await c.AddAsync("AAA");
        c.Now = Ist(2026, 10, 8, 11, 0);
        await c.Service().RunAsync(Settings(), null, default);
        Assert.True(c.Downloader.Calls.All(x => x.ExcludeFrom == Thu));
        Assert.True((await c.Bars.GetCoverageAsync(Timeframe.Daily)).All(x => x.LastDate < Thu));         // nothing for today was stored

        using var d = new Ctx(); await d.AddAsync("AAA");
        d.Now = Ist(2026, 10, 8, 17, 0);
        await d.Service().RunAsync(Settings(), null, default);
        Assert.True(d.Downloader.Calls.All(x => x.ExcludeFrom is null));
    }

    [Fact]
    public async Task Holiday_BenchmarkLastSession_PreventsRefetchingEveryStock()
    {
        using var c = new Ctx(); await c.AddAsync("AAA");
        var bench = c.Host.Options<BenchmarkOptions>().Value;
        // Market was closed Thursday (holiday): the index's newest bar is Wednesday, and AAA is cached through Wednesday.
        foreach (var sym in new[] { bench.BroadIndexSymbol, bench.MidcapIndexSymbol!, "AAA" })
            await c.Bars.UpsertRangeAsync(new[] { new HistoricalBar { Symbol = sym, Timestamp = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.FromHours(5.5)), Open = 1, High = 1, Low = 1, Close = 1, AdjustedClose = 1, Volume = 1, Source = "t", Timeframe = Timeframe.Daily } });
        c.Now = Ist(2026, 10, 8, 17, 0);
        c.Downloader.Holidays.Add(Thu);                            // the source returns no bar for the holiday
        // By weekday logic the benchmark looks one day behind, so it IS asked once; it returns nothing new, which tells the service the
        // market's real last session was Wednesday — so AAA (cached through Wednesday) must NOT be re-requested.
        var s = await c.Service().RunAsync(Settings(), null, default);
        Assert.True(c.Downloader.Calls.Any(x => x.Symbol == bench.BroadIndexSymbol));
        Assert.False(c.Downloader.Calls.Any(x => x.Symbol == "AAA"));
        Assert.True(s.UpToDate >= 1 && s.Failed == 0);
    }

    [Fact]
    public async Task OnlySymbols_And_NoBenchmarks_RestrictTheRun()
    {
        using var c = new Ctx(); await c.AddAsync("AAA", "BBB", "CCC");
        await c.Service().RunAsync(Settings(only: new[] { "bbb" }) with { IncludeBenchmarks = false }, null, default);
        Assert.Equal("BBB", string.Join(",", c.Downloader.Calls.Select(x => x.Symbol).Distinct()));
    }

    [Fact]
    public async Task KeepTurnover_LeavesTheInstrumentColumnAlone()
    {
        using var c = new Ctx(); await c.AddAsync("AAA");
        await c.Service().RunAsync(Settings(keepTurnover: true), null, default);
        Assert.Null((await c.Instruments.GetBySymbolAsync("AAA"))!.AverageDailyTurnover);
    }
}
