using TradingPlatform.Core.Enums;

namespace TradingPlatform.Core.Models;

/// <summary>Universe instrument / symbol-mapping record (spec §2.2).</summary>
public sealed class Instrument
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public required string NseSymbol { get; set; }
    public required string YahooSymbol { get; set; }
    public string? Isin { get; set; }
    public required string CompanyName { get; set; }

    /// <summary>Unknown by default — never silently defaulted (spec §2.2).</summary>
    public string? Sector { get; set; }
    public string? Industry { get; set; }

    public decimal? MarketCap { get; set; }
    public decimal? AverageDailyTurnover { get; set; }
    public int? LotSize { get; set; }
    public required string Exchange { get; set; }
    public bool Active { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
