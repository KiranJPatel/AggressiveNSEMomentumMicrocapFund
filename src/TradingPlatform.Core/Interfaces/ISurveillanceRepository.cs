namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Read/write access to ASM/GSM/surveillance flags (spec §2.3), ingested manually/via CSV.</summary>
public interface ISurveillanceRepository : ISurveillanceDataProvider
{
    Task UpsertAsync(SurveillanceStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest status with AsOfDate in [asOf - maxAgeDays, asOf]; an explicit Unknown when there is none (never "Safe").
    /// maxAgeDays = 0 reproduces the exact-date behaviour of GetStatusAsync. The window exists because ASM/GSM lists are published
    /// daily while a scan on Monday legitimately relies on Friday's list; it is bounded so a long-stale list can never read as current.
    /// </summary>
    Task<SurveillanceStatus> GetLatestStatusAsync(string symbol, DateOnly asOfDate, int maxAgeDays, CancellationToken cancellationToken = default);

    /// <summary>The most recent status ever recorded for the symbol (any date), or null — for `universe show`.</summary>
    Task<SurveillanceStatus?> GetMostRecentAsync(string symbol, CancellationToken cancellationToken = default);
}
