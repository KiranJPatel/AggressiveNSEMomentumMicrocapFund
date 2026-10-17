using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Yahoo;

namespace TradingPlatform.Application.Cli;

public sealed record IntradayDownloadSettings(Timeframe Timeframe, DateOnly To, int Days, bool IncludeToday, int MaxConsecutiveFailures, IReadOnlyList<string>? OnlySymbols, int SettlementMinutes);

public sealed record IntradayDownloadSummary(
    Timeframe Timeframe, DateOnly From, DateOnly To, int Planned, int Downloaded, int NoNewData, int Failed, int BarsPersisted,
    bool Aborted, string? AbortReason, IReadOnlyList<DownloadFailure> Failures, DateOnly? ProvisionalCutoff);

/// <summary>
/// Orchestrates intraday (60m / 15m / 5m) downloads for the active universe (spec §7.3). Resumable like the daily download: only weekdays
/// not yet cached are fetched, a failed symbol leaves its range missing for the next run, and a run of consecutive failures aborts
/// instead of hammering a dead network. Today's still-forming session is never stored.
/// </summary>
public sealed class IntradayDownloadService
{
    private readonly IInstrumentRepository _instruments;
    private readonly IIntradayBarDownloader _downloader;
    private readonly ScannerOptions _scanner;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    public IntradayDownloadService(IInstrumentRepository instruments, IIntradayBarDownloader downloader, ScannerOptions scanner, ILogger logger, Func<DateTimeOffset>? utcNow = null)
    {
        _instruments = instruments; _downloader = downloader; _scanner = scanner; _logger = logger; _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IntradayDownloadSummary> RunAsync(IntradayDownloadSettings s, Action<string>? progress, CancellationToken ct)
    {
        var cutoff = DownloadPlanner.ProvisionalCutoff(_utcNow(), _scanner, s.SettlementMinutes, s.IncludeToday);
        var to = DownloadPlanner.LastExpectedBarDate(s.To, cutoff);
        var days = Math.Min(s.Days, _downloader.MaxHistoryDays(s.Timeframe));
        var from = to.AddDays(-days);

        var requested = s.OnlySymbols is { Count: > 0 } ? s.OnlySymbols.Select(x => x.ToUpperInvariant()).ToHashSet() : null;
        var targets = (await _instruments.GetActiveAsync(ct)).Select(i => i.Symbol).Where(sym => requested is null || requested.Contains(sym.ToUpperInvariant())).ToList();

        int downloaded = 0, noNew = 0, bars = 0, consecutive = 0, done = 0;
        var failures = new List<DownloadFailure>();
        string? abortReason = null;

        foreach (var symbol in targets)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var result = await _downloader.DownloadIntradayAsync(symbol, from, to, s.Timeframe, cutoff, ct);
            if (!result.Success)
            {
                failures.Add(new DownloadFailure(symbol, result.Error ?? "unknown error"));
                consecutive++;
                progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} FAILED: {result.Error}");
                if (consecutive >= s.MaxConsecutiveFailures)
                {
                    abortReason = $"{consecutive} consecutive failures (last: {result.Error}) — the network or Yahoo is likely unavailable; aborting. Re-run later: it resumes where it left off.";
                    break;
                }
                continue;
            }

            consecutive = 0;
            if (result.BarsPersisted == 0) { noNew++; progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} up to date"); }
            else
            {
                downloaded++; bars += result.BarsPersisted;
                progress?.Invoke($"[{done,3}/{targets.Count}] {symbol,-14} +{result.BarsPersisted} {s.Timeframe} bars {result.FirstDate:yyyy-MM-dd}..{result.LastDate:yyyy-MM-dd}{(result.QualityIssues > 0 ? $"  ({result.QualityIssues} data-quality flag(s))" : "")}");
            }
        }

        _logger.LogInformation("download-intraday {Timeframe}: {Downloaded} symbols updated, {NoNew} current, {Failed} failed, {Bars} bars.", s.Timeframe, downloaded, noNew, failures.Count, bars);
        return new IntradayDownloadSummary(s.Timeframe, from, to, targets.Count, downloaded, noNew, failures.Count, bars, abortReason is not null, abortReason, failures, cutoff);
    }
}

/// <summary>`dotnet run -- download-intraday --timeframe 60m|15m|5m [--days N] [--symbols A,B] [--to yyyy-MM-dd] [--include-today] [--max-failures N]`</summary>
public static class IntradayDownloadCommand
{
    public static async Task<int> RunAsync(string[] args, IntradayDownloadService service, IIntradayBarDownloader downloader, ILogger logger, CancellationToken ct = default)
    {
        var (settings, error) = ParseArgs(args, DateOnly.FromDateTime(DateTime.UtcNow), downloader);
        if (settings is null) { Console.Error.WriteLine(error); return 1; }

        Console.WriteLine($"Downloading {settings.Timeframe} bars (Yahoo serves at most {downloader.MaxHistoryDays(settings.Timeframe)} days of this interval)...");
        var sw = Stopwatch.StartNew();
        var summary = await service.RunAsync(settings, Console.WriteLine, ct);
        Console.WriteLine("========================================================");
        Console.WriteLine($" DOWNLOAD-INTRADAY {summary.Timeframe}: {summary.From:yyyy-MM-dd}..{summary.To:yyyy-MM-dd}  ({sw.Elapsed.TotalSeconds:F0}s)");
        Console.WriteLine($" Symbols {summary.Planned}: updated {summary.Downloaded}, already current {summary.NoNewData}, failed {summary.Failed}; {summary.BarsPersisted:N0} bars stored");
        if (summary.ProvisionalCutoff is { } c) Console.WriteLine($" Today's session ({c:yyyy-MM-dd}) is still forming: not stored (use --include-today to override).");
        if (summary.Aborted) Console.WriteLine($" ABORTED: {summary.AbortReason}");
        foreach (var f in summary.Failures.Take(10)) Console.WriteLine($"   FAILED {f.Symbol}: {f.Error}");
        Console.WriteLine("========================================================");
        return summary.Aborted || summary.Failed > 0 && summary.Downloaded == 0 && summary.NoNewData == 0 ? 1 : 0;
    }

    internal static (IntradayDownloadSettings? Settings, string? Error) ParseArgs(string[] args, DateOnly today, IIntradayBarDownloader downloader)
    {
        string? Flag(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
        bool Has(string name) => Array.IndexOf(args, name) >= 0;

        foreach (var f in new[] { "--timeframe", "--days", "--symbols", "--to", "--max-failures" })
            if (Has(f) && Flag(f) is null) return (null, $"{f} needs a value.");

        if (Flag("--timeframe") is not { } tfText) return (null, "--timeframe is required: 60m, 15m or 5m.");
        if (!YahooIntradayLimits.TryParse(tfText, out var tf)) return (null, $"--timeframe '{tfText}' must be 60m, 15m or 5m.");

        var to = today;
        if (Flag("--to") is { } t && !DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)) return (null, $"--to '{t}' is not a valid date (yyyy-MM-dd).");

        var max = downloader.MaxHistoryDays(tf);
        var days = max;
        if (Flag("--days") is { } d && (!int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days < 1 || days > max))
            return (null, $"--days '{d}' must be an integer 1-{max} for {tfText} (Yahoo's intraday retention).");

        var maxFail = 10;
        if (Flag("--max-failures") is { } m && (!int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFail) || maxFail < 1)) return (null, $"--max-failures '{m}' must be a positive integer.");

        var symbols = Flag("--symbols")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (new IntradayDownloadSettings(tf, to, days, Has("--include-today"), maxFail, symbols, 20), null);
    }
}
