using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Scanner.Scheduling;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

public class DailyJobsTests
{
    private static readonly ScannerOptions Opt = new(); // 09:15–15:30 Asia/Kolkata, pre-lead 60, post-delay 15
    // 2026-10-05 is a Monday.
    private static DateTimeOffset Ist(int y, int m, int d, int h, int min) => new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.FromHours(5.5));

    // ---- schedule ----
    [Fact]
    public void PreMarket_IsOneHourBeforeOpen_AndPostMarket_FifteenMinAfterClose()
    {
        var now = Ist(2026, 10, 5, 6, 0);
        Assert.Equal(Ist(2026, 10, 5, 8, 15), DailyJobSchedule.NextRun(now, Opt, DailyJobKind.PreMarket));
        Assert.Equal(Ist(2026, 10, 5, 15, 45), DailyJobSchedule.NextRun(now, Opt, DailyJobKind.PostMarket));
    }

    [Fact]
    public void AfterTodaysSlot_RollsToNextWeekday()
    {
        var now = Ist(2026, 10, 5, 9, 0); // Monday 09:00 — Monday's 08:15 pre-market already passed
        Assert.Equal(Ist(2026, 10, 6, 8, 15), DailyJobSchedule.NextRun(now, Opt, DailyJobKind.PreMarket));
    }

    [Fact]
    public void Friday_Evening_And_Weekend_RollToMonday()
    {
        Assert.Equal(Ist(2026, 10, 12, 8, 15), DailyJobSchedule.NextRun(Ist(2026, 10, 9, 16, 0), Opt, DailyJobKind.PreMarket));   // Fri after post-market
        Assert.Equal(Ist(2026, 10, 12, 15, 45), DailyJobSchedule.NextRun(Ist(2026, 10, 10, 12, 0), Opt, DailyJobKind.PostMarket)); // Saturday
    }

    [Fact]
    public void Schedule_IsTimezoneCorrect_RegardlessOfCallerOffset()
    {
        var utc = Ist(2026, 10, 5, 6, 0).ToUniversalTime();
        Assert.Equal(Ist(2026, 10, 5, 8, 15).ToUniversalTime(), DailyJobSchedule.NextRun(utc, Opt, DailyJobKind.PreMarket));
    }

    // ---- runner ----
    private sealed class FakeMarketData : IBarDownloader
    {
        public List<string> Requested { get; } = new(); public HashSet<string> Fail { get; } = new();
        public Task<BarDownloadResult> DownloadAsync(string symbol, DateOnly from, DateOnly to, DateOnly? excludeFrom = null, CancellationToken ct = default)
        {
            Requested.Add(symbol);
            return Task.FromResult(Fail.Contains(symbol) ? BarDownloadResult.Failed(symbol, "HTTP 500") : new BarDownloadResult(symbol, true, 0, 0, null, null, null, 0));
        }
    }

    private sealed class CompositeProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner; private readonly Dictionary<Type, object> _overrides;
        public CompositeProvider(IServiceProvider inner, Dictionary<Type, object> overrides) { _inner = inner; _overrides = overrides; }
        public object? GetService(Type t) => _overrides.TryGetValue(t, out var o) ? o : _inner.GetService(t);
    }

    private sealed class Ctx : IDisposable
    {
        public readonly CliTestHost Host = CliTestHost.Create();
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "tp_jobs_" + Guid.NewGuid().ToString("N"));
        public readonly FakeMarketData Data = new();
        public readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public readonly DailyJobsRunner Runner;
        public string Backups => Path.Combine(Dir, "data", "backups");

        public Ctx(int retention = 14)
        {
            Directory.CreateDirectory(Dir);
            var sp = new CompositeProvider(Host.Provider, new Dictionary<Type, object>
            {
                [typeof(IBarDownloader)] = Data,
                [typeof(SqliteConnection)] = Host.Db,
                [typeof(IOptions<PathsOptions>)] = Options.Create(new PathsOptions { Data = Path.Combine(Dir, "data"), Reports = Path.Combine(Dir, "reports") }),
                [typeof(IOptions<ScannerOptions>)] = Options.Create(new ScannerOptions { DatabaseBackupRetentionCount = retention }),
            });
            Runner = new DailyJobsRunner(sp, NullLogger.Instance, () => Now);
        }
        public async Task AddInstrumentAsync(string symbol) =>
            await Host.Repo<IInstrumentRepository>().UpsertAsync(new Instrument { Symbol = symbol, NseSymbol = symbol, YahooSymbol = symbol + ".NS", CompanyName = symbol, Exchange = "NSE", Active = true });
        public void Dispose() { Host.Dispose(); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public async Task PreMarket_EmptyUniverse_FlagsValidation_StillRefreshesBenchmarks_AndRunsEveryStep()
    {
        using var c = new Ctx();
        var steps = await c.Runner.RunPreMarketAsync(default);
        Assert.Equal("DataRefresh,UniverseValidation,RegimeCalc,Reconciliation", string.Join(",", steps.Select(s => s.Name)));
        Assert.False(steps.Single(s => s.Name == "UniverseValidation").Ok);
        Assert.True(steps.Single(s => s.Name == "UniverseValidation").Detail.Contains("import --kind universe"));
        Assert.True(c.Data.Requested.Count >= 1);            // the benchmark index
        Assert.False(steps.Single(s => s.Name == "RegimeCalc").Ok);   // no cached bars -> reported, not thrown
        Assert.True(steps.Single(s => s.Name == "Reconciliation").Ok); // Paper mode: nothing to reconcile
    }

    [Fact]
    public async Task DataRefresh_OneSymbolFailing_IsReported_AndDoesNotStopTheOtherSteps()
    {
        using var c = new Ctx();
        await c.AddInstrumentAsync("ABC"); await c.AddInstrumentAsync("XYZ");
        c.Data.Fail.Add("ABC");
        var steps = await c.Runner.RunPreMarketAsync(default);
        var refresh = steps.Single(s => s.Name == "DataRefresh");
        Assert.False(refresh.Ok);
        Assert.True(refresh.Detail.Contains("ABC"));
        Assert.True(c.Data.Requested.Contains("XYZ"));       // the healthy symbol was still refreshed
        Assert.Equal(4, steps.Count);
    }

    [Fact]
    public async Task PostMarket_WritesVerifiedSnapshot_AndDailyReportJson()
    {
        using var c = new Ctx();
        var steps = await c.Runner.RunPostMarketAsync(default);
        Assert.Equal("Reconciliation,Performance,DbSnapshot,DailyReport", string.Join(",", steps.Select(s => s.Name)));
        Assert.True(steps.All(s => s.Ok), string.Join(" | ", steps.Where(s => !s.Ok).Select(s => $"{s.Name}: {s.Detail}")));

        var snap = Path.Combine(c.Backups, "tradingplatform-20261005.db");
        Assert.True(File.Exists(snap));
        using (var copy = new SqliteConnection(snap))           // the backup must itself be a readable database with our schema
            Assert.True(copy.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='Orders';", r => r.GetString("name")).Count == 1);

        var report = Path.Combine(c.Dir, "reports", "daily-2026-10-05.json");
        Assert.True(File.Exists(report));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("2026-10-05", doc.RootElement.GetProperty("Date").GetString());
        Assert.True(doc.RootElement.TryGetProperty("OpenPositions", out _));
    }

    [Fact]
    public async Task DbSnapshot_PrunesOldBackups_BeyondRetention_KeepingNewest()
    {
        using var c = new Ctx(retention: 2);
        Directory.CreateDirectory(c.Backups);
        foreach (var d in new[] { "20260101", "20260102", "20260103", "20260104" }) File.WriteAllText(Path.Combine(c.Backups, $"tradingplatform-{d}.db"), "x");
        await c.Runner.RunPostMarketAsync(default);
        var left = Directory.GetFiles(c.Backups).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal("tradingplatform-20260104.db,tradingplatform-20261005.db", string.Join(",", left));
    }

    [Fact]
    public async Task DbSnapshot_SameDayRerun_ReplacesTheSnapshot_NoCrash()
    {
        using var c = new Ctx();
        await c.Runner.RunPostMarketAsync(default);
        var again = await c.Runner.RunPostMarketAsync(default);
        Assert.True(again.Single(s => s.Name == "DbSnapshot").Ok);
    }
}
