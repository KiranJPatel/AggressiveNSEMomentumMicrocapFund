using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Abstractions;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Paper;

namespace TradingPlatform.Application.Cli;

public sealed record OverlayOutcome(IReadOnlyList<HistoricalBar>? Bars, HistoricalBar? ExecutionBar, string? Rejection, double? QuoteAgeSeconds)
{
    public bool Valid => Bars is not null;
}

/// <summary>
/// Turns a live quote into "today's forming daily bar" appended to the cached final bars, so the SAME indicator / VCP / breakout / stop
/// code that runs on daily history sees the current price. Pure and heavily validated: a quote that is stale, from another session,
/// implausible, or inconsistent with the cached history is REJECTED (the symbol then simply has no overlay) rather than trusted —
/// the failure mode is "trade on yesterday's data or not at all", never "trade on a wrong price".
/// </summary>
public static class RealtimeBarOverlay
{
    /// <summary>Fraction (0..1) of today's regular session that has elapsed, in the market's own time zone.</summary>
    public static decimal SessionFraction(DateTimeOffset nowUtc, ScannerOptions scanner)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(scanner.MarketTimeZoneId);
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz).TimeOfDay;
        var open = TimeOnly.Parse(scanner.MarketOpenLocalTime).ToTimeSpan();
        var close = TimeOnly.Parse(scanner.MarketCloseLocalTime).ToTimeSpan();
        if (close <= open) return 1m;
        var f = (local - open).TotalSeconds / (close - open).TotalSeconds;
        return (decimal)Math.Clamp(f, 0.0, 1.0);
    }

    public static DateOnly MarketDate(DateTimeOffset nowUtc, ScannerOptions scanner) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.FindSystemTimeZoneById(scanner.MarketTimeZoneId)).Date);

    public static OverlayOutcome Build(IReadOnlyList<HistoricalBar> daily, RealtimeQuote q, DateTimeOffset nowUtc, ScannerOptions scanner, RealtimeOptions options)
    {
        var age = (nowUtc - q.QuoteTimeUtc).TotalSeconds;
        OverlayOutcome Reject(string why) => new(null, null, why, age);

        if (q.LastPrice <= 0 || q.DayHigh <= 0 || q.DayLow <= 0 || q.PreviousClose <= 0) return Reject("quote has a non-positive price field");
        if (q.DayHigh < q.DayLow) return Reject($"day high {q.DayHigh} is below day low {q.DayLow}");
        if (age < -60) return Reject($"quote time is {-age:F0}s in the future (clock skew or bad feed)");
        if (age > options.MaxQuoteAgeSeconds) return Reject($"stale quote: {age:F0}s old (limit {options.MaxQuoteAgeSeconds}s) — a delayed feed looks exactly like this");

        var today = MarketDate(nowUtc, scanner);
        var quoteDate = MarketDate(q.QuoteTimeUtc, scanner);
        if (quoteDate != today) return Reject($"quote is from {quoteDate:yyyy-MM-dd}, not today's session ({today:yyyy-MM-dd})");

        var deviation = Math.Abs(q.LastPrice - q.PreviousClose) / q.PreviousClose * 100m;
        if (deviation > options.MaxDeviationFromPreviousClosePercent) return Reject($"price {q.LastPrice} is {deviation:F1}% from the previous close {q.PreviousClose} (limit {options.MaxDeviationFromPreviousClosePercent}%) — data error or corporate action");

        var history = daily.Where(b => DateOnly.FromDateTime(b.Timestamp.DateTime) < today).ToList(); // any provisional bar stored for today is replaced
        var prior = history.Count > 0 ? history[^1] : null;
        if (prior is not null)
        {
            var mismatch = Math.Abs(q.PreviousClose - prior.Close) / prior.Close * 100m;
            if (mismatch > options.MaxPreviousCloseMismatchPercent)
                return Reject($"previous close {q.PreviousClose} differs from the cached last close {prior.Close} ({prior.Timestamp:yyyy-MM-dd}) by {mismatch:F1}% — the cache is behind (run `download`) or a split/bonus has occurred");
        }

        var high = new[] { q.DayHigh, q.LastPrice, q.Open }.Max();
        var low = new[] { q.DayLow, q.LastPrice, q.Open > 0 ? q.Open : q.LastPrice }.Min();
        var fraction = SessionFraction(nowUtc, scanner);
        var volume = fraction >= options.MinSessionFractionForVolumeProjection && fraction > 0 ? q.Volume / fraction : q.Volume;

        var stamp = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), IndiaStandardTime.Offset);
        var bar = new HistoricalBar
        {
            Symbol = q.Symbol, Timestamp = stamp, Open = q.Open > 0 ? q.Open : q.LastPrice, High = high, Low = low, Close = q.LastPrice, AdjustedClose = q.LastPrice,
            Volume = (long)Math.Round(volume), Source = "realtime:" + q.Source, Timeframe = Timeframe.Daily, CreatedAt = nowUtc,
        };

        // Execution reference for PAPER fills: the market order fills at the LIVE price (not the stale last-bar open). Capacity (volume) is the larger of
        // today's so-far volume and the last full session's — early in the day "so far" is tiny and would manufacture partial fills that real market depth would not.
        var exec = new HistoricalBar
        {
            Symbol = q.Symbol, Timestamp = stamp, Open = q.LastPrice, High = high, Low = low, Close = q.LastPrice, AdjustedClose = q.LastPrice,
            Volume = (long)Math.Max(q.Volume, prior?.Volume ?? 0), Source = "realtime-exec:" + q.Source, Timeframe = Timeframe.Daily, CreatedAt = nowUtc,
        };

        var bars = new List<HistoricalBar>(history.Count + 1);
        bars.AddRange(history); bars.Add(bar);
        return new OverlayOutcome(bars, exec, null, age);
    }
}

/// <summary>
/// One cycle's real-time state: fetches quotes for a set of symbols, validates each into an overlay, and remembers which symbols got
/// one (and why the others did not) so every consumer — exit evaluation, scanner, paper fills — sees the same answer.
/// </summary>
public sealed class RealtimeSession
{
    private readonly IRealtimeQuoteProvider _provider;
    private readonly RealtimeOptions _options;
    private readonly ScannerOptions _scanner;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, OverlayOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noQuote = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);

    public RealtimeSession(IRealtimeQuoteProvider provider, RealtimeOptions options, ScannerOptions scanner, ILogger logger, Func<DateTimeOffset>? utcNow = null)
    { _provider = provider; _options = options; _scanner = scanner; _logger = logger; _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow); }

    public string SourceName => _provider.SourceName;

    /// <summary>Quotes any not-yet-requested symbols (priority order; capped by Realtime:MaxSymbolsPerCycle across the whole session).</summary>
    public async Task RefreshAsync(IEnumerable<string> symbols, Func<string, IReadOnlyList<HistoricalBar>> dailyBars, CancellationToken ct)
    {
        var room = Math.Max(0, _options.MaxSymbolsPerCycle - _requested.Count);
        var wanted = symbols.Where(s => !_requested.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(room).ToList();
        if (wanted.Count == 0) return;
        foreach (var s in wanted) _requested.Add(s);

        IReadOnlyDictionary<string, RealtimeQuote> quotes;
        try { quotes = await _provider.GetQuotesAsync(wanted, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "realtime: {Source} quote request failed entirely — falling back to daily bars for these symbols.", _provider.SourceName);
            foreach (var s in wanted) _noQuote.Add(s);
            return;
        }

        var now = _utcNow();
        foreach (var symbol in wanted)
        {
            if (!quotes.TryGetValue(symbol, out var quote)) { _noQuote.Add(symbol); continue; }
            var outcome = RealtimeBarOverlay.Build(dailyBars(symbol), quote, now, _scanner, _options);
            _outcomes[symbol] = outcome;
            if (!outcome.Valid) _logger.LogWarning("realtime: {Symbol} quote rejected — {Reason}", symbol, outcome.Rejection);
        }
    }

    public IReadOnlyList<HistoricalBar>? OverlayBars(string symbol) => _outcomes.TryGetValue(symbol, out var o) && o.Valid ? o.Bars : null;
    public HistoricalBar? ExecutionBar(string symbol) => _outcomes.TryGetValue(symbol, out var o) && o.Valid ? o.ExecutionBar : null;
    public string? WhyNoOverlay(string symbol) =>
        _outcomes.TryGetValue(symbol, out var o) ? (o.Valid ? null : o.Rejection) : _noQuote.Contains(symbol) ? $"no quote returned by {_provider.SourceName}" : "not requested this cycle";

    public int Requested => _requested.Count;
    public int ValidCount => _outcomes.Values.Count(o => o.Valid);
    public IReadOnlyList<(string Symbol, string Reason)> Problems =>
        _outcomes.Where(kv => !kv.Value.Valid).Select(kv => (kv.Key, kv.Value.Rejection!)).Concat(_noQuote.Select(s => (s, $"no quote returned by {_provider.SourceName}"))).OrderBy(x => x.Item1).ToList();

    public string Summary()
    {
        var ages = _outcomes.Values.Where(o => o.Valid && o.QuoteAgeSeconds is not null).Select(o => o.QuoteAgeSeconds!.Value).ToList();
        return $"{_provider.SourceName}: {ValidCount} valid of {Requested} requested" + (ages.Count > 0 ? $", quote age {ages.Min():F0}-{ages.Max():F0}s" : "") + (Problems.Count > 0 ? $", {Problems.Count} without a usable quote (fall back to daily bars / excluded from entries)" : "");
    }
}

/// <summary>Paper-fill quote source that prefers the live execution bar and otherwise falls back to the cached-bar source (and records that it did).</summary>
public sealed class RealtimeQuoteSource : IQuoteSource
{
    private readonly IQuoteSource _fallback;
    private readonly RealtimeSession _session;
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);

    public RealtimeQuoteSource(IQuoteSource fallback, RealtimeSession session) { _fallback = fallback; _session = session; }
    public IReadOnlyCollection<string> SymbolsFilledOnStaleData => _stale;

    public async Task<PaperQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var cached = await _fallback.GetQuoteAsync(symbol, cancellationToken);
        if (_session.ExecutionBar(symbol) is { } bar) return cached with { Bar = bar };
        _stale.Add(symbol);
        return cached;
    }
}
