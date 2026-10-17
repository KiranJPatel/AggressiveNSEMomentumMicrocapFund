namespace TradingPlatform.Core.Models;

/// <summary>What the bar cache holds for one symbol.</summary>
public sealed record BarCoverage(string Symbol, DateOnly FirstDate, DateOnly LastDate, int BarCount);
