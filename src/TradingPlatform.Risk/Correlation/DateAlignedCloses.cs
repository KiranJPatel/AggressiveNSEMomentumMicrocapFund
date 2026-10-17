using TradingPlatform.Core.Models;

namespace TradingPlatform.Risk.Correlation;

/// <summary>
/// Aligns two bar series by trading date before a correlation calculation
/// — the same "don't assume positional alignment, align by actual date"
/// reasoning as Strategy.RelativeStrength.PriceSeriesAligner (Tranche 5),
/// intentionally duplicated in miniature here rather than referenced: Risk
/// and Strategy are sibling layers that both feed into Backtesting (spec
/// §1.2's architecture), and Risk depending on Strategy just to reuse a
/// dozen lines would be a reverse-layering dependency for no real benefit.
/// </summary>
internal static class DateAlignedCloses
{
    public static (IReadOnlyList<decimal> First, IReadOnlyList<decimal> Second) Align(IReadOnlyList<HistoricalBar> firstBars, IReadOnlyList<HistoricalBar> secondBars)
    {
        var secondByDate = secondBars.ToDictionary(b => DateOnly.FromDateTime(b.Timestamp.DateTime), b => b.Close);

        var firstCloses = new List<decimal>();
        var secondCloses = new List<decimal>();

        foreach (var bar in firstBars.OrderBy(b => b.Timestamp))
        {
            var date = DateOnly.FromDateTime(bar.Timestamp.DateTime);
            if (secondByDate.TryGetValue(date, out var secondClose))
            {
                firstCloses.Add(bar.Close);
                secondCloses.Add(secondClose);
            }
        }

        return (firstCloses, secondCloses);
    }
}
