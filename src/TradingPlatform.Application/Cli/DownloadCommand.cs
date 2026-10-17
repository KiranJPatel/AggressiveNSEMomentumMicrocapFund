using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

public sealed record DownloadSettings(
    DateOnly To,
    int HistoryYears,
    int RefreshDays,
    bool Force,
    DateOnly? FromOverride,
    bool IncludeToday,
    bool KeepTurnover,
    int MaxConsecutiveFailures,
    IReadOnlyList<string>? OnlySymbols,
    bool IncludeBenchmarks,
    int SettlementMinutes);

public enum DownloadAction { Download, UpToDate }
public sealed record DownloadPlan(string Symbol, DownloadAction Action, DateOnly From, DateOnly To, string Reason);

public sealed record DownloadFailure(string Symbol, string Error);

public sealed record DownloadSummary(
    DateTimeOffset StartedAt, double ElapsedSeconds, int Planned, int Downloaded, int UpToDate, int NoNewData, int Failed, int BarsPersisted,
    bool Aborted, string? AbortReason, IReadOnlyList<DownloadFailure> Failures, DateOnly? ProvisionalCutoff);

/// <summary>
/// Pure planning for the `download` command (no I/O, so it is unit-tested): which date range does each symbol need, and is today's
/// bar provisional.
/// </summary>
public static class DownloadPlanner
{
    /// <summary>
    /// The date from which fetched bars must NOT be stored: today's (market-timezone) date while that session is still forming or has
    /// not yet settled. A bar for a session that is still trading carries partial volume and an unfinished close — storing it as final
    /// would corrupt every scan, indicator and backtest that reads it, and the cache would never refresh it. Null on weekends or once
    /// the session has settled (close + <paramref name="settlementMinutes"/>).
    /// </summary>
    public static DateOnly? ProvisionalCutoff(DateTimeOffset nowUtc, ScannerOptions scanner, int settlementMinutes, bool includeToday)
    {
        if (includeToday) return null;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(scanner.MarketTimeZoneId);
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        var settledAt = TimeOnly.Parse(scanner.MarketCloseLocalTime).ToTimeSpan() + TimeSpan.FromMinutes(settlementMinutes);
        return local.TimeOfDay >= settledAt ? null : DateOnly.FromDateTime(local.Date);
    }

    /// <summary>The newest date for which a bar can legitimately be expected: the last weekday on/before <paramref name="to"/> (strictly before the provisional cutoff).</summary>
    public static DateOnly LastExpectedBarDate(DateOnly to, DateOnly? provisionalCutoff)
    {
        var d = provisionalCutoff is { } cut && to >= cut ? cut.AddDays(-1) : to;
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    public static DownloadPlan Plan(string symbol, BarCoverage? coverage, DateOnly effectiveTo, DateOnly expectedLast, DownloadSettings s)
    {
        var defaultFrom = s.FromOverride ?? effectiveTo.AddYears(-s.HistoryYears);

        if (coverage is null)
            return new(symbol, DownloadAction.Download, defaultFrom, effectiveTo, $"no cached bars — full history from {defaultFrom:yyyy-MM-dd}");

        if (s.FromOverride is { } fo && fo < coverage.FirstDate)
            return new(symbol, DownloadAction.Download, fo, effectiveTo, $"back-filling from {fo:yyyy-MM-dd} (cache starts {coverage.FirstDate:yyyy-MM-dd})");

        if (s.Force)
            return new(symbol, DownloadAction.Download, coverage.LastDate.AddDays(-s.RefreshDays), effectiveTo, $"--force: re-fetching the last {s.RefreshDays} day(s)");

        if (coverage.LastDate >= expectedLast)
            return new(symbol, DownloadAction.UpToDate, coverage.LastDate, effectiveTo, $"cached through {coverage.LastDate:yyyy-MM-dd}");

        return new(symbol, DownloadAction.Download, coverage.LastDate.AddDays(-s.RefreshDays), effectiveTo,
            $"cached through {coverage.LastDate:yyyy-MM-dd}; re-fetching from {coverage.LastDate.AddDays(-s.RefreshDays):yyyy-MM-dd} (refresh overlap {s.RefreshDays}d)");
    }
}

/// <summary>
/// Downloads daily OHLC for the WHOLE universe (plus the benchmark indices) into the SQLite bar cache that backtest / optimize /
/// walkforward / montecarlo / scan / paper / live all read. Incremental and resumable (it only fetches what the cache lacks, plus a
/// small overlap so recently revised bars are replaced), failure-reporting (every failed symbol is listed with its reason), and it
/// never stores a still-forming session's bar. Benchmarks go first: their last bar date defines the market's last trading day, so
/// holidays do not make every stock look "behind".
/// </summary>
public sealed class DownloadService
{
    private readonly IInstrumentRepository _instruments;
    private readonly IHistoricalBarRepository _bars;
    private readonly IBarDownloader _downloader;
    private readonly IEngineStateRepository _state;
    private readonly BenchmarkOptions _benchmarks;
    private readonly ScannerOptions _scanner;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    public const string LastDownloadStateKey = "LastDownload";

    public DownloadService(IInstrumentRepository instruments, IHistoricalBarRepository bars, IBarDownloader downloader, IEngineStateRepository state,
        BenchmarkOptions benchmarks, ScannerOptions scanner, ILogger logger, Func<DateTimeOffset>? utcNow = null)
    {
        _instruments = instruments; _bars = bars; _downloader = downloader; _state = state; _benchmarks = benchmarks; _scanner = scanner;
        _logger = logger; _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DownloadSummary> RunAsync(DownloadSettings s, Action<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = _utcNow();
        var cutoff = DownloadPlanner.ProvisionalCutoff(startedAt, _scanner, s.SettlementMinutes, s.IncludeToday);
        var effectiveTo = s.To;
        var coverage = (await _bars.GetCoverageAsync(Timeframe.Daily, ct)).ToDictionary(c => c.Symbol, StringComparer.OrdinalIgnoreCase);

        // Targets: benchmarks first, then the active universe (or the requested subset).
        var benchmarkSymbols = s.IncludeBenchmarks
            ? new[] { _benchmarks.BroadIndexSymbol, _benchmarks.MidcapIndexSymbol }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToList()
            : new List<string>();
        var active = await _instruments.GetActiveAsync(ct);
        var requested = s.OnlySymbols is { Count: > 0 } ? s.OnlySymbols.Select(x => x.ToUpperInvariant()).ToHashSet() : null;
        var stockSymbols = active.Select(i => i.Symbol).Where(sym => requested is null || requested.Contains(sym.ToUpperInvariant())).ToList();
        var targets = benchmarkSymbols.Concat(stockSymbols).ToList();

        int downloaded = 0, upToDate = 0, noNew = 0, bars = 0, consecutive = 0, done = 0;
        var failures = new List<DownloadFailure>();
        string? abortReason = null;

        // Until the benchmark has been handled, fall back to "last weekday" as the expected last bar.
        var expectedLast = DownloadPlanner.LastExpectedBarDate(effectiveTo, cutoff);

        foreach (var symbol in targets)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var isBenchmark = benchmarkSymbols.Contains(symbol);
            coverage.TryGetValue(symbol, out var cov);
            var plan = DownloadPlanner.Plan(symbol, cov, effectiveTo, expectedLast, s);

            if (plan.Action == DownloadAction.UpToDate)
            {
                upToDate++;
                progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} up to date ({plan.Reason})");
                continue;
            }

            var result = await _downloader.DownloadAsync(symbol, plan.From, plan.To, cutoff, ct);
            if (!result.Success)
            {
                failures.Add(new DownloadFailure(symbol, result.Error ?? "unknown error"));
                consecutive++;
                progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} FAILED: {result.Error}");
                if (consecutive >= s.MaxConsecutiveFailures)
                {
                    abortReason = $"{consecutive} consecutive failures (last: {result.Error}) — the network or Yahoo is likely unavailable; aborting instead of retrying every remaining symbol. Re-run later: it resumes where it left off.";
                    break;
                }
                continue;
            }

            consecutive = 0;
            if (result.BarsPersisted == 0) { noNew++; progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} no new bars ({plan.Reason})"); }
            else
            {
                downloaded++; bars += result.BarsPersisted;
                progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} +{result.BarsPersisted} bars {result.FirstDate:yyyy-MM-dd}..{result.LastDate:yyyy-MM-dd}{(result.QualityIssues > 0 ? $"  ({result.QualityIssues} data-quality flag(s))" : "")}");
                coverage[symbol] = new BarCoverage(symbol, cov?.FirstDate ?? result.FirstDate!.Value, result.LastDate!.Value, (cov?.BarCount ?? 0) + result.BarsPersisted);
            }

            if (isBenchmark && symbol.Equals(_benchmarks.BroadIndexSymbol, StringComparison.OrdinalIgnoreCase) && coverage.TryGetValue(symbol, out var idx))
                expectedLast = idx.LastDate < expectedLast ? idx.LastDate : expectedLast; // the market's own last session beats "last weekday" (holidays)

            if (!isBenchmark && !s.KeepTurnover) await RefreshTurnoverAsync(symbol, ct);
        }

        var summary = new DownloadSummary(startedAt, sw.Elapsed.TotalSeconds, targets.Count, downloaded, upToDate, noNew, failures.Count, bars,
            abortReason is not null, abortReason, failures, cutoff);
        try { await _state.SetAsync(LastDownloadStateKey, JsonSerializer.Serialize(summary), ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist the download summary."); }
        return summary;
    }

    /// <summary>AverageDailyTurnover is DERIVED data (mean close x volume over the lookback), so it is recomputed from the bars just stored rather than trusted from a CSV that carried 0.</summary>
    private async Task RefreshTurnoverAsync(string symbol, CancellationToken ct)
    {
        var lookback = Math.Max(1, _scanner.LiquidityLookbackDays);
        var to = DateOnly.FromDateTime(_utcNow().UtcDateTime);
        var recent = await _bars.GetRangeAsync(symbol, to.AddDays(-(lookback * 2 + 10)), to, Timeframe.Daily, ct);
        if (recent.Count < Math.Min(5, lookback)) return;
        var window = recent.Skip(Math.Max(0, recent.Count - lookback)).ToList();
        await _instruments.UpdateDerivedMetricsAsync(symbol, Math.Round(window.Average(b => b.Close * b.Volume), 0), ct);
    }
}

/// <summary>
/// `dotnet run -- download [--symbols A,B] [--years N] [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--refresh-days N] [--force] [--include-today]
///                          [--no-benchmarks] [--keep-turnover] [--max-failures N]`
/// Exit codes: 0 all good, 2 finished with failures (or aborted), 1 usage error, 130 cancelled (Ctrl+C; safe to re-run).
/// </summary>
public static class DownloadCommand
{
    public static async Task<int> RunAsync(string[] args, DownloadService service, ScannerOptions scanner, ILogger logger, CancellationToken externalToken = default)
    {
        // "Today" is the MARKET's calendar date (IST), not UTC: between 18:30 and 24:00 UTC the NSE is already on the next day.
        var marketToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(scanner.MarketTimeZoneId)).Date);
        var (settings, error) = ParseArgs(args, marketToday);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("Cancelling after the current symbol… (progress is saved; re-run to resume)"); };
        Console.CancelKeyPress += onCancel;
        try
        {
            Console.WriteLine($"Downloading daily bars into the SQLite cache (to {settings!.To:yyyy-MM-dd}, default history {settings.HistoryYears}y, refresh overlap {settings.RefreshDays}d)…");
            var summary = await service.RunAsync(settings, Console.WriteLine, cts.Token);
            Print(summary);
            return summary.Aborted || summary.Failed > 0 ? 2 : 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Download cancelled."); return 130; }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    internal static (DownloadSettings? Settings, string? Error) ParseArgs(string[] args, DateOnly today)
    {
        string? Flag(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
        bool Has(string name) => args.Contains(name);
        foreach (var f in new[] { "--symbols", "--years", "--from", "--to", "--refresh-days", "--max-failures" })
            if (Has(f) && Flag(f) is null) return (null, $"{f} requires a value.");

        var to = today;
        if (Flag("--to") is { } t) { if (!DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)) return (null, $"--to '{t}' must be yyyy-MM-dd."); }
        DateOnly? from = null;
        if (Flag("--from") is { } f0) { if (!DateOnly.TryParseExact(f0, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd)) return (null, $"--from '{f0}' must be yyyy-MM-dd."); from = fd; }
        if (from is { } ff && ff > to) return (null, "--from is after --to.");
        var years = 5; var refresh = 5; var maxFail = 10;
        if (Flag("--years") is { } y && (!int.TryParse(y, out years) || years < 1 || years > 30)) return (null, $"--years '{y}' must be an integer 1-30.");
        if (Flag("--refresh-days") is { } r && (!int.TryParse(r, out refresh) || refresh < 0 || refresh > 60)) return (null, $"--refresh-days '{r}' must be an integer 0-60.");
        if (Flag("--max-failures") is { } m && (!int.TryParse(m, out maxFail) || maxFail < 1)) return (null, $"--max-failures '{m}' must be a positive integer.");
        var symbols = Flag("--symbols")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (new DownloadSettings(to, years, refresh, Has("--force"), from, Has("--include-today"), Has("--keep-turnover"), maxFail, symbols, !Has("--no-benchmarks"), 20), null);
    }

    private static void Print(DownloadSummary s)
    {
        Console.WriteLine("========================================================");
        Console.WriteLine($" DOWNLOAD SUMMARY — {s.Planned} target(s) in {s.ElapsedSeconds:F0}s");
        Console.WriteLine("========================================================");
        Console.WriteLine($"  downloaded {s.Downloaded,5}   ({s.BarsPersisted:N0} bars stored)");
        Console.WriteLine($"  up to date {s.UpToDate,5}");
        Console.WriteLine($"  no new bars{s.NoNewData,5}");
        Console.WriteLine($"  FAILED     {s.Failed,5}");
        if (s.ProvisionalCutoff is { } c) Console.WriteLine($"  Today's session ({c:yyyy-MM-dd}) is still forming/unsettled: its bar was NOT stored (use --include-today to override).");
        foreach (var f in s.Failures.Take(40)) Console.WriteLine($"    {f.Symbol,-14} {f.Error}");
        if (s.Failures.Count > 40) Console.WriteLine($"    … and {s.Failures.Count - 40} more");
        if (s.Aborted) Console.Error.WriteLine($"  ABORTED: {s.AbortReason}");
    }
}
