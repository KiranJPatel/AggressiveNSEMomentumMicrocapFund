namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Institutional/delivery activity signal, explicitly nullable-with-reason (spec §3.2).</summary>
public sealed class InstitutionalActivitySnapshot
{
    public required string Symbol { get; set; }
    public DateOnly AsOfDate { get; set; }
    public decimal? ActivityScore { get; set; }
    public DataAvailability Availability { get; set; }
}
