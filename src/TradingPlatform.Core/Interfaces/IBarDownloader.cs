using TradingPlatform.Core.Models;

namespace TradingPlatform.Core.Interfaces;

/// <summary>Outcome of fetching + validating + persisting one symbol's daily bars. Unlike IHistoricalMarketDataProvider.GetBarsAsync (cache-first, failures swallowed), this REPORTS failure.</summary>
public sealed record BarDownloadResult(
    string Symbol, bool Success, int BarsFetched, int BarsPersisted, DateOnly? FirstDate, DateOnly? LastDate, string? Error, int QualityIssues)
{
    public static BarDownloadResult Failed(string symbol, string error) => new(symbol, false, 0, 0, null, null, error, 0);
}

/// <summary>
/// Always-fetch, always-report daily-bar download (used by the `download` command and the pre-market refresh). It never consults the
/// cache to decide whether to fetch — the CALLER decides the range (so recent, possibly-revised bars can be re-fetched) — and it
/// upserts, so re-downloading a range is idempotent.
/// </summary>
public interface IBarDownloader
{
    /// <param name="excludeFrom">Bars dated on/after this are fetched but NOT persisted (a provisional, still-forming session bar must never be stored as final).</param>
    Task<BarDownloadResult> DownloadAsync(string symbol, DateOnly from, DateOnly to, DateOnly? excludeFrom = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Intraday (60m / 15m / 5m) bar download for the multi-timeframe layers (spec §7.3). Separate from <see cref="IBarDownloader"/> so existing
/// daily-only implementations and test fakes are untouched. Yahoo only serves a short window of intraday history (about 59 days for 5m/15m,
/// 729 for 60m): older ranges are clamped, and the caller is told how far back data can actually go via <see cref="MaxHistoryDays"/>.
/// </summary>
public interface IIntradayBarDownloader
{
    /// <summary>Furthest-back history (calendar days) the source can serve for this timeframe.</summary>
    int MaxHistoryDays(TradingPlatform.Core.Enums.Timeframe timeframe);

    /// <summary>Fetches only the weekdays in [from, to] that are not already cached for this timeframe; <paramref name="excludeFrom"/> works as in <see cref="IBarDownloader"/>.</summary>
    Task<BarDownloadResult> DownloadIntradayAsync(string symbol, DateOnly from, DateOnly to, TradingPlatform.Core.Enums.Timeframe timeframe, DateOnly? excludeFrom = null, CancellationToken cancellationToken = default);
}
