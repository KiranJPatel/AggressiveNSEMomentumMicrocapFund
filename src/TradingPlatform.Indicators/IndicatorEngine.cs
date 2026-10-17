using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Momentum;
using TradingPlatform.Indicators.MovingAverages;
using TradingPlatform.Indicators.Trend;
using TradingPlatform.Indicators.Volatility;
using TradingPlatform.Indicators.Volume;

namespace TradingPlatform.Indicators;

/// <summary>
/// Orchestrates every indicator in this project into one IndicatorSnapshot
/// per bar, matching Core.Models.IndicatorSnapshot field-for-field (spec
/// §3.4, §7.2). Bars must be a single symbol/timeframe, sorted ascending by
/// Timestamp — the caller (Strategy/Scanner tranches) owns fetching and
/// ordering; this is a pure function with no I/O.
/// </summary>
public static class IndicatorEngine
{
    public static IReadOnlyList<IndicatorSnapshot> ComputeSeries(IReadOnlyList<HistoricalBar> bars, IndicatorOptions options)
    {
        if (bars.Count == 0) return Array.Empty<IndicatorSnapshot>();

        var closes = bars.Select(b => b.Close).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        var now = DateTimeOffset.UtcNow;

        var hmaFast = HullMovingAverage.Calculate(closes, options.HmaFastPeriod);
        var hmaSlow = HullMovingAverage.Calculate(closes, options.HmaSlowPeriod);
        var supertrend = SupertrendIndicator.Calculate(bars, options.SupertrendAtrPeriod, options.SupertrendMultiplier);
        var sma20 = SimpleMovingAverage.Calculate(closes, options.Sma20Period);
        var sma50 = SimpleMovingAverage.Calculate(closes, options.Sma50Period);
        var sma200 = SimpleMovingAverage.Calculate(closes, options.Sma200Period);
        var rsi = RelativeStrengthIndex.Calculate(closes, options.RsiPeriod);
        var macd = Macd.Calculate(closes, options.MacdFastPeriod, options.MacdSlowPeriod, options.MacdSignalPeriod);
        var adx = AverageDirectionalIndex.Calculate(bars, options.AdxPeriod);
        var atr = AverageTrueRange.Calculate(bars, options.AtrPeriod);
        var atrPercent = AverageTrueRange.CalculatePercent(bars, options.AtrPeriod);
        var hv = HistoricalVolatility.Calculate(closes, options.HistoricalVolatilityPeriod, options.TradingDaysPerYear);
        var volumeSma = VolumeIndicators.VolumeSma(volumes, options.VolumeSmaPeriod);
        var relativeVolume = VolumeIndicators.RelativeVolume(volumes, options.VolumeSmaPeriod);

        var snapshots = new IndicatorSnapshot[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            snapshots[i] = new IndicatorSnapshot
            {
                Symbol = bars[i].Symbol,
                Timestamp = bars[i].Timestamp,
                Timeframe = bars[i].Timeframe,
                HmaFast = hmaFast[i],
                HmaSlow = hmaSlow[i],
                Supertrend = supertrend[i].Value,
                Sma20 = sma20[i],
                Sma50 = sma50[i],
                Sma200 = sma200[i],
                Rsi = rsi[i],
                Macd = macd.MacdLine[i],
                MacdSignal = macd.SignalLine[i],
                Adx = adx.Adx[i],
                Atr = atr[i],
                AtrPercent = atrPercent[i],
                HistoricalVolatility = hv[i],
                VolumeSma = volumeSma[i],
                RelativeVolume = relativeVolume[i],
                CreatedAt = now
            };
        }

        return snapshots;
    }
}
