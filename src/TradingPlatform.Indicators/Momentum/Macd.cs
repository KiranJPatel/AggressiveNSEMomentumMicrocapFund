using TradingPlatform.Indicators.MovingAverages;

namespace TradingPlatform.Indicators.Momentum;

public sealed record MacdResult(IReadOnlyList<decimal?> MacdLine, IReadOnlyList<decimal?> SignalLine, IReadOnlyList<decimal?> Histogram);

/// <summary>
/// MACD (Gerald Appel): MacdLine = EMA(fast) - EMA(slow); SignalLine =
/// EMA(signalPeriod) of MacdLine; Histogram = MacdLine - SignalLine.
/// Standard defaults 12/26/9.
/// </summary>
public static class Macd
{
    public static MacdResult Calculate(IReadOnlyList<decimal> closes, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        var emaFast = ExponentialMovingAverage.Calculate(closes, fastPeriod);
        var emaSlow = ExponentialMovingAverage.Calculate(closes, slowPeriod);

        var macdLine = new decimal?[closes.Count];
        for (var i = 0; i < closes.Count; i++)
        {
            if (emaFast[i].HasValue && emaSlow[i].HasValue)
                macdLine[i] = emaFast[i]!.Value - emaSlow[i]!.Value;
        }

        var firstValid = Array.FindIndex(macdLine, v => v.HasValue);
        var signalLine = new decimal?[closes.Count];

        if (firstValid >= 0)
        {
            var macdTail = macdLine.Skip(firstValid).Select(v => v!.Value).ToList();
            var signalTail = ExponentialMovingAverage.Calculate(macdTail, signalPeriod);
            for (var i = 0; i < signalTail.Count; i++)
                signalLine[firstValid + i] = signalTail[i];
        }

        var histogram = new decimal?[closes.Count];
        for (var i = 0; i < closes.Count; i++)
        {
            if (macdLine[i].HasValue && signalLine[i].HasValue)
                histogram[i] = macdLine[i]!.Value - signalLine[i]!.Value;
        }

        return new MacdResult(macdLine, signalLine, histogram);
    }
}
