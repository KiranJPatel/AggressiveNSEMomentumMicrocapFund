using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.MovingAverages;
using TradingPlatform.Indicators.Volatility;

namespace TradingPlatform.Indicators.Trend;

public sealed record AdxResult(IReadOnlyList<decimal?> PlusDi, IReadOnlyList<decimal?> MinusDi, IReadOnlyList<decimal?> Adx);

/// <summary>
/// Wilder's Average Directional Index (New Concepts in Technical Trading
/// Systems, 1978) — the third of Wilder's three sibling indicators sharing
/// his smoothing method (alongside ATR and RSI). Standard, well-established
/// formula:
///   +DM[i] = High[i]-High[i-1], if that exceeds (Low[i-1]-Low[i]) and is positive; else 0
///   -DM[i] = Low[i-1]-Low[i], if that exceeds (High[i]-High[i-1]) and is positive; else 0
///   Smooth +DM, -DM, and True Range with Wilder's smoothing (period N)
///   +DI = 100 * Smoothed(+DM) / Smoothed(TR)
///   -DI = 100 * Smoothed(-DM) / Smoothed(TR)
///   DX  = 100 * |+DI - -DI| / (+DI + -DI)
///   ADX = Wilder-smoothed average of DX over period N
/// </summary>
public static class AverageDirectionalIndex
{
    public static AdxResult Calculate(IReadOnlyList<HistoricalBar> bars, int period)
    {
        var n = bars.Count;
        var plusDm = new decimal[n];
        var minusDm = new decimal[n];

        for (var i = 1; i < n; i++)
        {
            var upMove = bars[i].High - bars[i - 1].High;
            var downMove = bars[i - 1].Low - bars[i].Low;

            plusDm[i] = upMove > downMove && upMove > 0 ? upMove : 0;
            minusDm[i] = downMove > upMove && downMove > 0 ? downMove : 0;
        }

        var trueRanges = TrueRange.Calculate(bars);
        var smoothedTr = ExponentialMovingAverage.CalculateWilder(trueRanges, period);
        var smoothedPlusDm = ExponentialMovingAverage.CalculateWilder(plusDm, period);
        var smoothedMinusDm = ExponentialMovingAverage.CalculateWilder(minusDm, period);

        var plusDi = new decimal?[n];
        var minusDi = new decimal?[n];
        var dx = new decimal?[n];

        for (var i = 0; i < n; i++)
        {
            if (!smoothedTr[i].HasValue || smoothedTr[i]!.Value == 0) continue;

            plusDi[i] = 100m * smoothedPlusDm[i]!.Value / smoothedTr[i]!.Value;
            minusDi[i] = 100m * smoothedMinusDm[i]!.Value / smoothedTr[i]!.Value;

            var diSum = plusDi[i]!.Value + minusDi[i]!.Value;
            dx[i] = diSum == 0 ? 0m : 100m * Math.Abs(plusDi[i]!.Value - minusDi[i]!.Value) / diSum;
        }

        // ADX itself is a Wilder smoothing of DX — but CalculateWilder expects
        // a dense decimal[] with no gaps, so compact the valid DX tail first
        // and re-expand the result back to the original index alignment.
        var firstValidDx = Array.FindIndex(dx, v => v.HasValue);
        var adx = new decimal?[n];

        if (firstValidDx >= 0)
        {
            var dxTail = dx.Skip(firstValidDx).Select(v => v!.Value).ToList();
            var adxTail = ExponentialMovingAverage.CalculateWilder(dxTail, period);
            for (var i = 0; i < adxTail.Count; i++)
                adx[firstValidDx + i] = adxTail[i];
        }

        return new AdxResult(plusDi, minusDi, adx);
    }
}
