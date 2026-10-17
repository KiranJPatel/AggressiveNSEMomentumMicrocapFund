namespace TradingPlatform.Risk.Analytics;

/// <summary>
/// Aggregate trade statistics (spec §4.4). AverageWinner/AverageLoser and
/// Expectancy are expressed in R-multiples (consistent with the rest of
/// this section — R-multiples are comparable across trades regardless of
/// position size, whereas raw currency figures aren't); ProfitFactor is
/// the one standard exception, conventionally a ratio of gross currency
/// profit to gross currency loss, not R-multiples (spec doesn't say
/// otherwise, and redefining a universally-standard metric would be more
/// confusing than useful).
/// Any field is null when there's no data to compute it from (e.g. no
/// losing trades yet) — never a fabricated 0 standing in for "undefined".
/// </summary>
public sealed record TradeAnalyticsSummary(
    int TradeCount,
    decimal? AverageR,
    decimal? MedianR,
    decimal? WinRatePercent,
    decimal? AverageWinnerR,
    decimal? AverageLoserR,
    decimal? ProfitFactor,
    decimal? ExpectancyR);
