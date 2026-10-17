using TradingPlatform.Core.Enums;

namespace TradingPlatform.Core.Models;

/// <summary>
/// Point-in-time AMFI/SEBI market-cap-bucket and sector classification for
/// an instrument, with an effective date range (spec §2.2). A backtest for
/// a given date must resolve the bucket that was effective on that date,
/// not today's classification.
/// </summary>
public sealed class UniverseClassificationSnapshot
{
    public long Id { get; set; }
    public long InstrumentId { get; set; }
    public required string Symbol { get; set; }

    public MarketCapBucket MarketCapBucket { get; set; }
    public int? MarketCapRank { get; set; }
    public string? Sector { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>True if this snapshot's bucket/sector was unresolvable for the period (spec §2.2) rather than inferred.</summary>
    public bool IsUnknown { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
