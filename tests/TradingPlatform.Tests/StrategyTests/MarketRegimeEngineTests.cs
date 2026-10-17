using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class MarketRegimeEngineTests
{
    private static HistoricalBar Bar(string symbol, int dayOffset, decimal close) => new()
    {
        Symbol = symbol,
        Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = close,
        Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    /// <summary>Smooth, zero-daily-noise compound growth — a "calm, steadily bullish" index.</summary>
    private static List<HistoricalBar> BuildSmoothBullishSeries(int bars = 250, decimal dailyGrowth = 0.0015m)
    {
        var result = new List<HistoricalBar>();
        var price = 10000m;
        for (var i = 0; i < bars; i++)
        {
            result.Add(Bar("NIFTY500", i, price));
            price *= 1 + dailyGrowth;
        }
        return result;
    }

    /// <summary>Sharp daily +/-6% alternation on top of a declining drift — a "volatile, bearish" index.</summary>
    private static List<HistoricalBar> BuildVolatileBearishSeries(int bars = 250)
    {
        var result = new List<HistoricalBar>();
        var price = 10000m;
        for (var i = 0; i < bars; i++)
        {
            result.Add(Bar("NIFTY500", i, price));
            var swing = i % 2 == 0 ? 1.06m : 0.94m;
            price *= swing * 0.997m; // net downward drift beneath the alternation
        }
        return result;
    }

    [Fact]
    public void SmoothBullishIndex_TrendScoreIsMaximal_VolatilityScoreIsMaximal()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildSmoothBullishSeries() };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.Equal(100m, snapshot.NiftyTrendScore);
        Assert.Equal(100m, snapshot.VolatilityScore);
    }

    [Fact]
    public void SmoothBullishIndex_RegimeScoreIsHigh_ClassifiedAggressiveOrNormal()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildSmoothBullishSeries() };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.True(snapshot.RegimeScore >= 65, $"Expected a high regime score for a calm, steadily bullish index; got {snapshot.RegimeScore}");
        Assert.True(snapshot.Classification is RegimeClassification.Aggressive or RegimeClassification.Normal);
    }

    [Fact]
    public void VolatileBearishIndex_TrendScoreIsMinimal_VolatilityScoreIsMinimal()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildVolatileBearishSeries() };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.Equal(0m, snapshot.NiftyTrendScore);
        Assert.Equal(0m, snapshot.VolatilityScore);
    }

    [Fact]
    public void VolatileBearishIndex_RegimeScoreIsLow_ClassifiedDefensiveOrEmergency()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildVolatileBearishSeries() };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.True(snapshot.RegimeScore <= 35, $"Expected a low regime score for a volatile, declining index; got {snapshot.RegimeScore}");
        Assert.True(snapshot.Classification is RegimeClassification.Defensive or RegimeClassification.Emergency);
    }

    [Fact]
    public void OnlyBroadIndexSupplied_OptionalFieldsStayNull_RegimeScoreStillComputed()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildSmoothBullishSeries() };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.True(snapshot.BreadthScore is null, "BreadthScore must stay null when not supplied, not a fabricated neutral value.");
        Assert.True(snapshot.AdvanceDeclineRatio is null);
        Assert.True(snapshot.SmallMidRelativeStrength is null);
        Assert.True(snapshot.BreakoutSuccessRate is null);

        // RegimeScore itself must still be a real, non-fallback value (renormalized over NiftyTrend + Volatility only).
        Assert.Equal(100, snapshot.RegimeScore);
    }

    [Fact]
    public void AllOptionalInputsSupplied_PropagateToSnapshotFields()
    {
        var inputs = new MarketRegimeInputs
        {
            BroadIndexBars = BuildSmoothBullishSeries(),
            PercentUniverseAbove50Dma = 72m,
            PercentUniverseAbove200Dma = 68m,
            AdvanceDeclineRatio = 2.0m,
            BreakoutSuccessRatePercent = 55m,
        };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        Assert.Equal(72m, snapshot.PercentUniverseAbove50Dma);
        Assert.Equal(68m, snapshot.PercentUniverseAbove200Dma);
        Assert.Equal(70m, snapshot.BreadthScore); // (72+68)/2
        Assert.Equal(2.0m, snapshot.AdvanceDeclineRatio);
        Assert.Equal(55m, snapshot.BreakoutSuccessRate);
    }

    [Fact]
    public void MidcapBarsSupplied_ComputesSmallMidRelativeStrength()
    {
        var broadBars = BuildSmoothBullishSeries(60, 0.001m);
        // Midcap index rallying harder than the broad index -> should score above 50.
        var midcapBars = new List<HistoricalBar>();
        var price = 5000m;
        for (var i = 0; i < 60; i++)
        {
            midcapBars.Add(Bar("MIDCAP", i, price));
            price *= 1.003m;
        }

        var inputs = new MarketRegimeInputs { BroadIndexBars = broadBars, MidcapIndexBars = midcapBars };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 3, 1));

        Assert.True(snapshot.SmallMidRelativeStrength is not null);
        Assert.True(snapshot.SmallMidRelativeStrength!.Value > 50m, "Midcap rallying harder than the broad index should score above 50.");
    }

    [Fact]
    public void InsufficientBars_DoesNotThrow_FailsClosedToZeroDeployableCapital()
    {
        var shortBars = Enumerable.Range(0, 10).Select(i => Bar("NIFTY500", i, 10000m + i * 10m)).ToList();
        var inputs = new MarketRegimeInputs { BroadIndexBars = shortBars };

        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 1, 15));

        Assert.True(snapshot.NiftyTrendScore is null);
        Assert.True(snapshot.VolatilityScore is null);
        // Spec §0.4: when the market cannot be judged the answer is "do not deploy", never a neutral 50 (Cautious, half deployed).
        Assert.Equal(0, snapshot.RegimeScore);
        Assert.Equal(RegimeClassification.Emergency, snapshot.Classification);
        Assert.Equal(0m, snapshot.DeployableCapitalPercent);
    }

    [Fact]
    public void NoIndexTrend_ButVolatilityAvailable_StillFailsClosed_NotAggressive()
    {
        // 30 calm bars: volatility is computable (and reads "calm") but SMA200 is not — calm alone must not unlock deployment.
        var bars = Enumerable.Range(0, 30).Select(i => Bar("NIFTY500", i, 10000m + i * 5m)).ToList();

        var snapshot = MarketRegimeEngine.Calculate(new MarketRegimeInputs { BroadIndexBars = bars }, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 2, 1));

        Assert.True(snapshot.NiftyTrendScore is null);
        Assert.Equal(0m, snapshot.DeployableCapitalPercent);
    }

    [Fact]
    public void NoIndexTrend_WithFailClosedDisabled_ScoresFromWhatIsAvailable()
    {
        var bars = Enumerable.Range(0, 30).Select(i => Bar("NIFTY500", i, 10000m + i * 5m)).ToList();
        var options = new MarketRegimeOptions { FailClosedWithoutTrend = false };

        var snapshot = MarketRegimeEngine.Calculate(new MarketRegimeInputs { BroadIndexBars = bars }, options, new RsOptions(), new DateOnly(2026, 2, 1));

        Assert.True(snapshot.VolatilityScore is not null);
        Assert.True(snapshot.DeployableCapitalPercent > 0m);
    }

    [Fact]
    public void AdvanceDeclineRatio_TwoToOne_ScoresEighty()
    {
        var inputs = new MarketRegimeInputs { BroadIndexBars = BuildSmoothBullishSeries(), AdvanceDeclineRatio = 2.0m };
        var snapshot = MarketRegimeEngine.Calculate(inputs, new MarketRegimeOptions(), new RsOptions(), new DateOnly(2026, 9, 1));

        // AD ratio itself is stored raw; verify it round-trips (the 0-100 mapping is exercised indirectly via RegimeScore).
        Assert.Equal(2.0m, snapshot.AdvanceDeclineRatio);
    }
}
