using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Validation;

namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// IHistoricalMarketDataProvider implementation backed by Yahoo Finance,
/// with a SQLite cache in front of it (spec §5.2). Flow: check cache →
/// determine missing ranges → download only missing ranges → validate →
/// persist → return the authoritative cached range. Only Daily timeframe is
/// supported for now — Yahoo's intraday chart endpoint has a materially
/// different shape (short lookback windows, different interval params) and
/// is deferred; requesting a non-Daily timeframe throws NotSupportedException
/// rather than silently returning wrong/empty data.
/// </summary>
public sealed class YahooFinanceHistoricalDataProvider : IHistoricalMarketDataProvider, IBarDownloader, IIntradayBarDownloader
{
    private const string SourceName = "Yahoo";

    private readonly IYahooHttpFetcher _fetcher;
    private readonly IHistoricalBarRepository _repository;
    private readonly ISystemEventWriter _systemEventWriter;
    private readonly YahooFinanceOptions _options;
    private readonly ILogger<YahooFinanceHistoricalDataProvider> _logger;

    public YahooFinanceHistoricalDataProvider(
        IYahooHttpFetcher fetcher,
        IHistoricalBarRepository repository,
        ISystemEventWriter systemEventWriter,
        YahooFinanceOptions options,
        ILogger<YahooFinanceHistoricalDataProvider> logger)
    {
        _fetcher = fetcher;
        _repository = repository;
        _systemEventWriter = systemEventWriter;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HistoricalBar>> GetBarsAsync(string symbol, DateOnly from, DateOnly to, Timeframe timeframe, CancellationToken cancellationToken = default)
    {
        if (timeframe != Timeframe.Daily)
            throw new NotSupportedException($"YahooFinanceHistoricalDataProvider currently supports only {Timeframe.Daily}; {timeframe} is deferred (spec §7.3 multi-timeframe).");

        // Tranche 18: index tickers (Yahoo's universal "^" prefix
        // convention, e.g. "^CRSLDX" for Nifty 500 — see BenchmarkOptions)
        // never take an exchange suffix on Yahoo. Appending ".NS" to one
        // would silently request a non-existent symbol and return no data,
        // rather than throwing anywhere an operator would notice — worth
        // guarding explicitly now that BenchmarkOptions/BacktestCommand
        // actually fetch/read these.
        var yahooSymbol = symbol.StartsWith('^') || symbol.EndsWith(_options.DefaultSymbolSuffix, StringComparison.OrdinalIgnoreCase)
            ? symbol
            : symbol + _options.DefaultSymbolSuffix;

        var cached = await _repository.GetRangeAsync(symbol, from, to, timeframe, cancellationToken).ConfigureAwait(false);
        var cachedDates = cached.Select(b => DateOnly.FromDateTime(b.Timestamp.DateTime)).ToHashSet();

        var missingRanges = MissingRangeCalculator.Calculate(from, to, cachedDates);

        foreach (var (rangeFrom, rangeTo) in missingRanges)
        {
            await FetchValidateAndPersistAsync(symbol, yahooSymbol, rangeFrom, rangeTo, timeframe, null, cancellationToken).ConfigureAwait(false);
        }

        // Re-read from the repository so the return value is always the
        // authoritative persisted set, not an in-memory merge that could
        // drift from what's actually on disk.
        return await _repository.GetRangeAsync(symbol, from, to, timeframe, cancellationToken).ConfigureAwait(false);
    }

    public Task<BarDownloadResult> DownloadAsync(string symbol, DateOnly from, DateOnly to, DateOnly? excludeFrom = null, CancellationToken cancellationToken = default)
    {
        if (to < from) return Task.FromResult(BarDownloadResult.Failed(symbol, $"'to' ({to}) is before 'from' ({from})."));
        var yahooSymbol = YahooSymbolFor(symbol);
        return FetchValidateAndPersistAsync(symbol, yahooSymbol, from, to, Timeframe.Daily, excludeFrom, cancellationToken);
    }

    public int MaxHistoryDays(Timeframe timeframe) => YahooIntradayLimits.MaxHistoryDays(timeframe);

    public async Task<BarDownloadResult> DownloadIntradayAsync(string symbol, DateOnly from, DateOnly to, Timeframe timeframe, DateOnly? excludeFrom = null, CancellationToken cancellationToken = default)
    {
        if (!IsIntraday(timeframe)) return BarDownloadResult.Failed(symbol, $"{timeframe} is not an intraday timeframe.");
        if (_fetcher is not IYahooIntervalFetcher)
            return BarDownloadResult.Failed(symbol, "the configured Yahoo fetcher does not support intraday intervals.");
        if (to < from) return BarDownloadResult.Failed(symbol, $"'to' ({to}) is before 'from' ({from}).");

        // Yahoo refuses (or silently truncates) requests older than its intraday retention: clamp instead of asking for what cannot exist.
        var oldest = to.AddDays(-YahooIntradayLimits.MaxHistoryDays(timeframe));
        if (from < oldest)
        {
            _logger.LogWarning("{Symbol}: {Timeframe} history older than {Oldest:yyyy-MM-dd} is not served by Yahoo; clamping the range.", symbol, timeframe, oldest);
            from = oldest;
        }

        var yahooSymbol = YahooSymbolFor(symbol);
        var cached = await _repository.GetRangeAsync(symbol, from, to, timeframe, cancellationToken).ConfigureAwait(false);
        var cachedDates = cached.Select(b => DateOnly.FromDateTime(b.Timestamp.DateTime)).ToHashSet();
        // A day only counts as cached if it has bars; the provisional (still-forming) session is never stored, so it is always "missing".
        var missing = MissingRangeCalculator.Calculate(from, to, cachedDates);
        if (missing.Count == 0) return new BarDownloadResult(symbol, true, 0, 0, null, null, null, 0);

        int fetched = 0, persisted = 0, issues = 0;
        DateOnly? first = null, last = null;
        foreach (var (rangeFrom, rangeTo) in missing)
        {
            var r = await FetchValidateAndPersistAsync(symbol, yahooSymbol, rangeFrom, rangeTo, timeframe, excludeFrom, cancellationToken).ConfigureAwait(false);
            if (!r.Success) return r;   // partial progress stays cached; the failed range stays missing for the next run
            fetched += r.BarsFetched; persisted += r.BarsPersisted; issues += r.QualityIssues;
            if (r.FirstDate is { } f && (first is null || f < first)) first = f;
            if (r.LastDate is { } l && (last is null || l > last)) last = l;
        }
        return new BarDownloadResult(symbol, true, fetched, persisted, first, last, null, issues);
    }

    private static bool IsIntraday(Timeframe tf) => tf is Timeframe.H1 or Timeframe.M15 or Timeframe.M5;

    private string YahooSymbolFor(string symbol) =>
        symbol.StartsWith('^') || symbol.EndsWith(_options.DefaultSymbolSuffix, StringComparison.OrdinalIgnoreCase) ? symbol : symbol + _options.DefaultSymbolSuffix;

    private async Task<BarDownloadResult> FetchValidateAndPersistAsync(string symbol, string yahooSymbol, DateOnly from, DateOnly to, Timeframe timeframe, DateOnly? excludeFrom, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching {Symbol} ({YahooSymbol}) from {From} to {To}", symbol, yahooSymbol, from, to);

        string json;
        try
        {
            json = IsIntraday(timeframe) && _fetcher is IYahooIntervalFetcher intervalFetcher
                ? await intervalFetcher.FetchIntervalRawJsonAsync(yahooSymbol, from, to, YahooIntradayLimits.IntervalToken(timeframe), cancellationToken).ConfigureAwait(false)
                : await _fetcher.FetchRawJsonAsync(yahooSymbol, from, to, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch {Symbol} from Yahoo for {From}..{To} after retries", symbol, from, to);
            await LogIssueAsync(new DataQualityIssue(symbol, DateTimeOffset.UtcNow, DataQualitySeverity.Error, "FetchFailed", ex.Message), cancellationToken).ConfigureAwait(false);
            return BarDownloadResult.Failed(symbol, ex is YahooHttpException h ? $"HTTP {h.StatusCode}" : $"{ex.GetType().Name}: {ex.Message}"); // Range stays missing; a later call retries it.
        }

        YahooParseResult parseResult;
        try
        {
            parseResult = YahooChartResponseParser.Parse(json, symbol, SourceName, timeframe);
        }
        catch (YahooResponseException ex)
        {
            _logger.LogError(ex, "Failed to parse Yahoo response for {Symbol} ({From}..{To})", symbol, from, to);
            await LogIssueAsync(new DataQualityIssue(symbol, DateTimeOffset.UtcNow, DataQualitySeverity.Error, "ParseFailed", ex.Message), cancellationToken).ConfigureAwait(false);
            return BarDownloadResult.Failed(symbol, $"unparseable response: {ex.Message}");
        }

        foreach (var warning in parseResult.Warnings)
            _logger.LogWarning("{Symbol}: {Warning}", symbol, warning);

        var fetched = parseResult.Bars.Count;
        var usable = excludeFrom is { } cut ? parseResult.Bars.Where(b => DateOnly.FromDateTime(b.Timestamp.DateTime) < cut).ToList() : parseResult.Bars.ToList();

        if (usable.Count == 0)
        {
            _logger.LogInformation("{Symbol}: no bars to store for {From}..{To} (holiday range, provisional-only, or genuinely no data).", symbol, from, to);
            return new BarDownloadResult(symbol, true, fetched, 0, null, null, null, 0);
        }

        var orderedBars = usable.OrderBy(b => b.Timestamp).ToList();

        var qualityIssues = HistoricalBarValidator.Validate(orderedBars)
            .Concat(CorporateActionDetector.Detect(orderedBars, _options.ExtremeGapPercentThreshold))
            .ToList();

        if (IsIntraday(timeframe))
        {
            // Thin stocks routinely have empty 5m/15m candles: one Info line per bar would flood SystemEvents, so zero-volume candles are
            // reported as a single count (the bars themselves are still stored as received).
            var zeroVolume = qualityIssues.Where(i => i.IssueType == "ZeroVolume").ToList();
            if (zeroVolume.Count > 0)
            {
                qualityIssues = qualityIssues.Where(i => i.IssueType != "ZeroVolume").ToList();
                _logger.LogInformation("{Symbol}: {Count} zero-volume {Timeframe} candle(s) in {From}..{To} (stored as received).", symbol, zeroVolume.Count, timeframe, from, to);
            }
        }

        foreach (var issue in qualityIssues)
        {
            var logLevel = issue.Severity switch
            {
                DataQualitySeverity.Error => LogLevel.Error,
                DataQualitySeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information
            };
            _logger.Log(logLevel, "{Symbol} [{IssueType}] {Description}", issue.Symbol, issue.IssueType, issue.Description);
            await LogIssueAsync(issue, cancellationToken).ConfigureAwait(false);
        }

        // Bars are persisted as-received even when flagged — spec §5.3:
        // "never silently fix suspicious data", which cuts both ways: never
        // silently alter it AND never silently drop it. Flagging is the fix.
        await _repository.UpsertRangeAsync(orderedBars, cancellationToken).ConfigureAwait(false);
        return new BarDownloadResult(symbol, true, fetched, orderedBars.Count,
            DateOnly.FromDateTime(orderedBars[0].Timestamp.DateTime), DateOnly.FromDateTime(orderedBars[^1].Timestamp.DateTime), null, qualityIssues.Count);
    }

    private Task LogIssueAsync(DataQualityIssue issue, CancellationToken cancellationToken) =>
        _systemEventWriter.WriteAsync(new SystemEvent
        {
            Level = issue.Severity.ToString(),
            Category = "DataQuality",
            Message = $"[{issue.IssueType}] {issue.Symbol} @ {issue.Timestamp:O}: {issue.Description}",
            Timestamp = DateTimeOffset.UtcNow
        }, cancellationToken);
}
