namespace TradingPlatform.Core.Models;

/// <summary>A structured log record persisted to the SQLite system-event sink (spec §7.2).</summary>
public sealed class SystemEvent
{
    public long Id { get; set; }
    public required string Level { get; set; } // Debug/Info/Warning/Error/Fatal
    public required string Category { get; set; }
    public required string Message { get; set; }
    public string? Exception { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
