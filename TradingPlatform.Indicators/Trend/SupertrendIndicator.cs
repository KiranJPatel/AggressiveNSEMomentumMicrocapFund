using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Volatility;

namespace TradingPlatform.Indicators.Trend;

public sealed record SupertrendPoint(decimal? Value, bool? IsUpTrend);

/// <summary>
/// Supertrend (Olivier Seban). Confirmed formula (TradingView's published
/// reference implementation, the de-facto industry-standard version):
///   hl2 = (High + Low) / 2
///   basicUpperBand = hl2 + multiplier * ATR
///   basicLowerBand = hl2 - multiplier * ATR
///   upperBand[i] = (basicUpperBand[i] < upperBand[i-1]) OR (Close[i-1] > upperBand[i-1])
///                    ? basicUpperBand[i] : upperBand[i-1]
///   lowerBand[i] = (basicLowerBand[i] > lowerBand[i-1]) OR (Close[i-1] < lowerBand[i-1])
///                    ? basicLowerBand[i] : lowerBand[i-1]
///   trend direction flips when Close crosses the currently-active band;
///   Supertrend = lowerBand while up-trending, upperBand while down-trending.
/// Standard defaults: ATR period 10, multiplier 3.
/// </summary>
public static class SupertrendIndicator
{
    public static IReadOnlyList<SupertrendPoint> Calculate(IReadOnlyList<HistoricalBar> bars, int atrPeriod, decimal multiplier)
    {
        var n = bars.Count;
        var atr = AverageTrueRange.Calculate(bars, atrPeriod);
        var result = new SupertrendPoint[n];

        decimal? upperBand = null, lowerBand = null;
        bool? isUpTrend = null;

        for (var i = 0; i < n; i++)
        {
            if (!atr[i].HasValue) { result[i] = new SupertrendPoint(null, null); continue; }

            var hl2 = (bars[i].High + bars[i].Low) / 2m;
            var basicUpper = hl2 + multiplier * atr[i]!.Value;
            var basicLower = hl2 - multiplier * atr[i]!.Value;

            if (upperBand is null || lowerBand is null)
            {
                // First bar with a valid ATR: seed both bands directly from the basic bands (no prior band to ratchet against).
                upperBand = basicUpper;
                lowerBand = basicLower;
                isUpTrend = bars[i].Close > upperBand;
                result[i] = new SupertrendPoint(isUpTrend == true ? lowerBand : upperBand, isUpTrend);
                continue;
            }

            var prevClose = bars[i - 1].Close;

            upperBand = (basicUpper < upperBand.Value || prevClose > upperBand.Value) ? basicUpper : upperBand.Value;
            lowerBand = (basicLower > lowerBand.Value || prevClose < lowerBand.Value) ? basicLower : lowerBand.Value;

            var prevSupertrend = result[i - 1].Value;
            if (prevSupertrend == upperBand)
            {
                isUpTrend = bars[i].Close > upperBand;
            }
            else
            {
                isUpTrend = !(bars[i].Close < lowerBand);
            }

            var value = isUpTrend == true ? lowerBand : upperBand;
            result[i] = new SupertrendPoint(value, isUpTrend);
        }

        return result;
    }
}
