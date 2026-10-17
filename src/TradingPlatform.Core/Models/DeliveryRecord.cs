namespace TradingPlatform.Core.Models;

/// <summary>One symbol's delivery statistics for one trading session (NSE publishes them after the close). DeliveryPercent is 0-100.</summary>
public sealed class DeliveryRecord
{
    public long Id { get; set; }
    public required string Symbol { get; init; }
    public required DateOnly TradeDate { get; init; }
    public long? TotalTradedQuantity { get; init; }
    public long? DeliverableQuantity { get; init; }
    public required decimal DeliveryPercent { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Spec §3.4 delivery %: how much of the day's volume was taken for delivery, versus that stock's own recent norm. Ratio &gt; 1 = unusually high conviction.</summary>
public sealed record DeliverySnapshot(decimal TodayPercent, decimal AveragePercent, decimal Ratio, int SampleDays);
