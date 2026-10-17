using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Scanner.Scheduling;
using TradingPlatform.Strategy.Regime;

namespace TradingPlatform.Application.Cli;

public sealed record JobStep(string Name, bool Ok, string Detail);

/// <summary>
/// The spec §7.1 non-intraday jobs.
///   Pre-market:  data refresh, universe validation, regime calc, reconciliation
///   Post-market: reconciliation, performance summary, DB snapshot (+retention, integrity-checked), daily report (JSON)
/// Every step is isolated (one failing step never stops the rest) and reported; any failed step raises a SystemFailure
/// notification. None of these steps place orders or change positions.
/// </summary>
public sealed class DailyJobsRunner
{
    private readonly IInstrumentRepository _instruments;
    private readonly IHistoricalBarRepository _bars;
    private readonly IUniverseClassificationRepository _classifications;
    private readonly ISurveillanceRepository _surveillance;
    private readonly IPositionRepository _positions;
    private readonly IPortfolioSnapshotRepository _snapshots;
    private readonly ITradeRepository _trades;
    private readonly IRiskEventRepository _riskEvents;
    private readonly IHistoricalMarketDataProvider _marketData;
    private readonly SqliteConnection _db;
    private readonly IServiceProvider _provider;
    private readonly INotificationService? _notifier;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    public DailyJobsRunner(IServiceProvider provider, ILogger logger, Func<DateTimeOffset>? utcNow = null)
    {
        _provider = provider; _logger = logger; _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        T Get<T>() where T : notnull => (T)provider.GetService(typeof(T))!;
        _instruments = Get<IInstrumentRepository>(); _bars = Get<IHistoricalBarRepository>(); _classifications = Get<IUniverseClassificationRepository>();
        _surveillance = Get<ISurveillanceRepository>(); _positions = Get<IPositionRepository>(); _snapshots = Get<IPortfolioSnapshotRepository>();
        _trades = Get<ITradeRepository>(); _riskEvents = Get<IRiskEventRepository>(); _marketData = Get<IHistoricalMarketDataProvider>();
        _db = Get<SqliteConnection>(); _notifier = provider.GetService(typeof(INotificationService)) as INotificationService;
    }

    private T Opt<T>() where T : class, new() => ((IOptions<T>)_provider.GetService(typeof(IOptions<T>))!).Value;

    public async Task<IReadOnlyList<JobStep>> RunPreMarketAsync(CancellationToken ct)
    {
        var steps = new List<JobStep>
        {
            await Step("DataRefresh", () => DataRefreshAsync(ct)),
            await Step("UniverseValidation", () => UniverseValidationAsync(ct)),
            await Step("RegimeCalc", () => RegimeAsync(ct)),
            await Step("Reconciliation", () => ReconcileAsync(ct)),
        };
        await ReportAsync("Pre-market", steps, ct);
        return steps;
    }

    public async Task<IReadOnlyList<JobStep>> RunPostMarketAsync(CancellationToken ct)
    {
        var steps = new List<JobStep>
        {
            await Step("Reconciliation", () => ReconcileAsync(ct)),
            await Step("Performance", () => PerformanceAsync(ct)),
            await Step("DbSnapshot", () => DbSnapshotAsync(ct)),
        };
        steps.Add(await Step("DailyReport", () => DailyReportAsync(steps, ct)));
        await ReportAsync("Post-market", steps, ct);
        return steps;
    }

    private static async Task<JobStep> Step(string name, Func<Task<(bool Ok, string Detail)>> body)
    {
        try { var (ok, detail) = await body(); return new JobStep(name, ok, detail); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new JobStep(name, false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task ReportAsync(string job, IReadOnlyList<JobStep> steps, CancellationToken ct)
    {
        foreach (var s in steps) _logger.Log(s.Ok ? LogLevel.Information : LogLevel.Warning, "{Job} step {Step}: {Ok} — {Detail}", job, s.Name, s.Ok ? "ok" : "FAILED", s.Detail);
        if (_notifier is null) return;
        var failed = steps.Where(s => !s.Ok).ToList();
        if (failed.Count > 0) await _notifier.NotifyAsync(NotificationCategories.SystemFailure, $"{job} job: {failed.Count} step(s) failed — " + string.Join("; ", failed.Select(f => $"{f.Name}: {f.Detail}")), ct);
        else await _notifier.NotifyAsync("DailyJob", $"{job} job complete — " + string.Join("; ", steps.Select(s => $"{s.Name}: {s.Detail}")), ct);
    }

    // ---------------- pre-market ----------------

    private async Task<(bool, string)> DataRefreshAsync(CancellationToken ct)
    {
        // The same incremental, provisional-day-aware, failure-reporting path as `dotnet run -- download`.
        var downloader = _provider.GetService(typeof(IBarDownloader)) as IBarDownloader;
        if (downloader is null) return (false, "no bar downloader is registered");
        var scanner = Opt<ScannerOptions>();
        var service = new DownloadService(_instruments, _bars, downloader, (IEngineStateRepository)_provider.GetService(typeof(IEngineStateRepository))!, Opt<BenchmarkOptions>(), scanner, _logger, _utcNow);
        var marketToday = RealtimeBarOverlay.MarketDate(_utcNow(), scanner);
        var s = await service.RunAsync(new DownloadSettings(marketToday, 5, 5, false, null, false, false, 10, null, true, 20), null, ct);
        var detail = $"{s.Downloaded} downloaded ({s.BarsPersisted:N0} bars), {s.UpToDate} up to date, {s.Failed} failed";
        if (s.Failures.Count > 0) detail += ": " + string.Join(", ", s.Failures.Take(5).Select(f => $"{f.Symbol} ({f.Error})")) + (s.Failures.Count > 5 ? ", …" : "");
        if (s.Aborted) detail += $" — ABORTED: {s.AbortReason}";
        return (s.Failed == 0 && !s.Aborted, detail);
    }

    private async Task<(bool, string)> UniverseValidationAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_utcNow().UtcDateTime);
        var active = await _instruments.GetActiveAsync(ct);
        if (active.Count == 0) return (false, "no active instruments — run `import --kind universe` first");
        int withBars = 0, stale = 0, unknownBucket = 0, unknownSurveillance = 0;
        foreach (var i in active)
        {
            var recent = await _bars.GetRangeAsync(i.Symbol, today.AddDays(-30), today, Timeframe.Daily, ct);
            if (recent.Count > 0)
            {
                withBars++;
                if (DateOnly.FromDateTime(recent[^1].Timestamp.Date) < today.AddDays(-5)) stale++;
            }
            if ((await _classifications.GetClassificationAsync(i.Symbol, today, ct))?.MarketCapBucket is null or MarketCapBucket.Unknown) unknownBucket++;
            if ((await _surveillance.GetLatestStatusAsync(i.Symbol, today, Opt<ScannerOptions>().SurveillanceMaxStalenessDays, ct)).Availability == DataAvailability.Unknown) unknownSurveillance++;
        }
        var ok = withBars > 0 && stale == 0;
        return (ok, $"{active.Count} active, {withBars} with recent bars, {stale} stale (>5d), {unknownBucket} unclassified, {unknownSurveillance} without today's surveillance status");
    }

    private async Task<(bool, string)> RegimeAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_utcNow().UtcDateTime);
        var (broad, midcap) = await UniverseDataLoader.LoadBenchmarksAsync(_bars, Opt<BenchmarkOptions>(), today.AddDays(-500), today, _logger, ct);
        if (broad.Count == 0) return (false, "no cached broad-index bars");
        var asOf = DateOnly.FromDateTime(broad[^1].Timestamp.Date);
        var regimeOptions = Opt<MarketRegimeOptions>();
        var observations = new List<BreadthObservation>();
        foreach (var i in await _instruments.GetActiveAsync(ct))
        {
            var symBars = await _bars.GetRangeAsync(i.Symbol, asOf.AddDays(-400), asOf, Timeframe.Daily, ct);
            if (BreadthObservation.FromBars(symBars, asOf) is { } o) observations.Add(o);
        }
        var breadth = UniverseBreadthCalculator.Calculate(observations, regimeOptions.BreadthMinimumSampleSize);
        var breakout = BreakoutSuccessRateCalculator.Calculate(await _trades.GetLiveTradesAsync(ct), asOf, regimeOptions);
        var snap = MarketRegimeEngine.Calculate(new MarketRegimeInputs
        {
            BroadIndexBars = broad, MidcapIndexBars = midcap,
            PercentUniverseAbove50Dma = breadth.PercentAbove50Dma, PercentUniverseAbove200Dma = breadth.PercentAbove200Dma,
            AdvanceDeclineRatio = breadth.AdvanceDeclineRatio, BreakoutSuccessRatePercent = breakout,
        }, regimeOptions, Opt<RsOptions>(), asOf);
        if (_provider.GetService(typeof(IMarketRegimeRepository)) is IMarketRegimeRepository regimeRepo) await regimeRepo.UpsertAsync(snap, ct);
        return (true, $"{snap.Classification} (score {snap.RegimeScore}, deployable {snap.DeployableCapitalPercent:F0}%)");
    }

    private async Task<(bool, string)> ReconcileAsync(CancellationToken ct)
    {
        var live = Opt<LiveTradingOptions>();
        if (live.Mode != TradingMode.Live) return (true, $"{live.Mode} mode — no broker to reconcile against");
        var rc = await ReconcileCommand.RunAsync(_positions, _snapshots, _riskEvents, live, Opt<ZerodhaOptions>(), Opt<PortfolioOptions>(), _provider, _logger, ct);
        return (rc == 0, rc == 0 ? "clean" : "MISMATCH or broker unreachable — see reconcile output / RiskEvents");
    }

    // ---------------- post-market ----------------

    private async Task<(bool, string)> PerformanceAsync(CancellationToken ct)
    {
        var trades = await _trades.GetLiveTradesAsync(ct);
        var snap = await _snapshots.GetLatestAsync(ct);
        var nav = snap is null ? "no snapshot yet" : $"NAV {snap.Nav:N0}, drawdown {snap.DrawdownPercent:F1}%, {snap.OpenPositionCount} open";
        if (trades.Count == 0) return (true, $"{nav}; no closed trades yet");
        var wins = trades.Count(t => t.RealizedPnl > 0);
        return (true, $"{nav}; {trades.Count} closed trade(s), win rate {wins * 100m / trades.Count:F0}%, net P&L {trades.Sum(t => t.RealizedPnl):N0}, avg R {trades.Average(t => t.RMultiple):F2}");
    }

    private Task<(bool, string)> DbSnapshotAsync(CancellationToken ct)
    {
        var dir = RepoPaths.Combine(Path.Combine(Opt<PathsOptions>().Data, "backups"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"tradingplatform-{_utcNow():yyyyMMdd}.db");
        if (File.Exists(path)) File.Delete(path); // VACUUM INTO refuses an existing file; a same-day re-run replaces the snapshot
        _db.Execute($"VACUUM INTO '{path.Replace("'", "''")}';");

        // A backup that was never opened is not a backup: verify it is a readable, intact database.
        string verdict;
        using (var copy = new SqliteConnection(path))
            verdict = copy.Query("PRAGMA integrity_check;", r => r.GetString("integrity_check")).FirstOrDefault() ?? "no result";
        if (verdict != "ok") return Task.FromResult((false, $"snapshot {Path.GetFileName(path)} FAILED integrity check: {verdict}"));

        var keep = Math.Max(1, Opt<ScannerOptions>().DatabaseBackupRetentionCount);
        var old = new DirectoryInfo(dir).GetFiles("tradingplatform-*.db").OrderByDescending(f => f.Name).Skip(keep).ToList();
        foreach (var f in old) f.Delete();
        return Task.FromResult((true, $"{Path.GetFileName(path)} verified ({new FileInfo(path).Length / 1024} KB); kept {keep}, pruned {old.Count}"));
    }

    private async Task<(bool, string)> DailyReportAsync(IReadOnlyList<JobStep> priorSteps, CancellationToken ct)
    {
        var dir = RepoPaths.Combine(Opt<PathsOptions>().Reports);
        Directory.CreateDirectory(dir);
        var now = _utcNow();
        var snap = await _snapshots.GetLatestAsync(ct);
        var open = await _positions.GetOpenAsync(ct);
        var trades = await _trades.GetLiveTradesAsync(ct);
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var report = new
        {
            Date = now.ToString("yyyy-MM-dd"), GeneratedAtUtc = now,
            Nav = snap?.Nav, Cash = snap?.CashBalance, DrawdownPercent = snap?.DrawdownPercent, HeatPercent = snap?.PortfolioHeatPercent,
            OpenPositions = open.Select(p => new { p.Symbol, p.Quantity, p.AverageEntryPrice, p.CurrentStop, Stage = p.HighestStageFilled.ToString() }),
            TradesClosedToday = trades.Where(t => t.ExitAt >= todayStart).Select(t => new { t.Symbol, t.EntryPrice, t.ExitPrice, t.RealizedPnl, t.RMultiple, Reason = t.ExitReason.ToString() }),
            ClosedTradesTotal = trades.Count, NetRealizedPnl = trades.Sum(t => t.RealizedPnl),
            RiskEventsToday = (await _riskEvents.GetRecentAsync(200, ct)).Where(e => e.Timestamp >= todayStart).Select(e => new { e.EventType, e.Description, e.Timestamp }),
            JobSteps = priorSteps.Select(s => new { s.Name, s.Ok, s.Detail }),
        };
        var file = Path.Combine(dir, $"daily-{now:yyyy-MM-dd}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        return (true, Path.GetFileName(file));
    }
}

/// <summary>Hosted service (opt-in via `serve --scheduler`) that sleeps until the next pre-/post-market slot and runs the job; fault-isolated like ScannerBackgroundService.</summary>
public sealed class DailyJobsBackgroundService : BackgroundService
{
    private readonly DailyJobsRunner _runner;
    private readonly IOptions<ScannerOptions> _options;
    private readonly ILogger<DailyJobsBackgroundService> _logger;
    private readonly TimeProvider _time;

    public DailyJobsBackgroundService(DailyJobsRunner runner, IOptions<ScannerOptions> options, ILogger<DailyJobsBackgroundService> logger, TimeProvider? time = null)
    { _runner = runner; _options = options; _logger = logger; _time = time ?? TimeProvider.System; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var pre = DailyJobSchedule.NextRun(now, _options.Value, DailyJobKind.PreMarket);
            var post = DailyJobSchedule.NextRun(now, _options.Value, DailyJobKind.PostMarket);
            var (kind, at) = pre <= post ? (DailyJobKind.PreMarket, pre) : (DailyJobKind.PostMarket, post);
            _logger.LogInformation("Next {Kind} job at {At:u}.", kind, at);
            try
            {
                await Task.Delay(at - now, _time, stoppingToken).ConfigureAwait(false);
                if (kind == DailyJobKind.PreMarket) await _runner.RunPreMarketAsync(stoppingToken).ConfigureAwait(false);
                else await _runner.RunPostMarketAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "{Kind} job crashed — the schedule continues.", kind); await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken).ConfigureAwait(false); }
        }
    }
}
