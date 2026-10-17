using TradingPlatform.Indicators.MovingAverages;

namespace TradingPlatform.Indicators.Volume;

/// <summary>Volume SMA and relative volume (current volume / volume SMA) — spec §3.4 "Volume".</summary>
public static class VolumeIndicators
{
    public static IReadOnlyList<decimal?> VolumeSma(IReadOnlyList<long> volumes, int period) =>
        SimpleMovingAverage.Calculate(volumes.Select(v => (decimal)v).ToList(), period);

    public static IReadOnlyList<decimal?> RelativeVolume(IReadOnlyList<long> volumes, int period)
    {
        var sma = VolumeSma(volumes, period);
        var result = new decimal?[volumes.Count];
        for (var i = 0; i < volumes.Count; i++)
        {
            if (sma[i].HasValue && sma[i]!.Value != 0)
                result[i] = volumes[i] / sma[i]!.Value;
        }
        return result;
    }
}
