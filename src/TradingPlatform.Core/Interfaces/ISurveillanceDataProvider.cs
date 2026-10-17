namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// ASM/GSM/surveillance-flag source (spec §2.3) — manual/CSV/SQLite ingestion
/// initially. Never fabricated from Yahoo. Unknown stays Unknown, never "Safe".
/// </summary>
public interface ISurveillanceDataProvider
{
    Task<SurveillanceStatus> GetStatusAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default);
}
