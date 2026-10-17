using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Scoring;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class StockScoringEngineTests
{
    private static HistoricalBar Bar(int dayOffset, decimal close, long volume = 100000) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = close,
        Volume = volume, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot Snapshot(int dayOffset, decimal? relativeVolume = 1m, decimal? hmaFast = null,
        decimal? hmaSlow = null, decimal? sma50 = null, decimal? sma200 = null, decimal? supertrend = null, decimal? adx = null) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Timeframe = Timeframe.Daily, RelativeVolume = relativeVolume,
        HmaFast = hmaFast, HmaSlow = hmaSlow, Sma50 = sma50, Sma200 = sma200, Supertrend = supertrend, Adx = adx
    };

    private static StockScoringInputs BuildBaselineInputs(int bars = 65)
    {
        var barList = Enumerable.Range(0, bars).Select(i => Bar(i, 100m + i * 0.5m)).ToList();
        var snapshots = Enumerable.Range(0, bars).Select(i => Snapshot(i, relativeVolume: 1.2m, hmaFast: 105m, hmaSlow: 100m, sma50: 100m, sma200: 90m, supertrend: 95m, adx: 25m)).ToList();

        return new StockScoringInputs
        {
            Symbol = "STK",
            AsOfDate = new DateOnly(2026, 3, 1),
            Bars = barList,
            IndicatorSnapshots = snapshots,
            VcpPattern = null,
            RelativeStrengthScore = 75m,
            SectorStrengthScore = 60m,
            CurrentFundamentals = null,
            PriorFundamentals = null,
            InstitutionalActivity = null,
        };
    }

    [Fact]
    public void TotalScore_IsWithinZeroToOneHundred()
    {
        var score = StockScoringEngine.Calculate(BuildBaselineInputs(), new StrategyOptions(), new StockScoringOptions());
        Assert.True(score.TotalScore is >= 0m and <= 100m, $"TotalScore {score.TotalScore} out of bounds");
    }

    [Fact]
    public void TotalScore_MatchesManualWeightedSum()
    {
        var inputs = BuildBaselineInputs();
        var strategyOptions = new StrategyOptions();
        var score = StockScoringEngine.Calculate(inputs, strategyOptions, new StockScoringOptions());

        var w = strategyOptions.Weights;
        var expected = (
            score.RelativeStrengthScore * w.RelativeStrength +
            score.VolumeScore * w.VolumeAccumulation +
            score.PriceMomentumScore * w.PriceMomentum +
            score.EarningsAccelerationScore * w.EarningsAcceleration +
            score.VcpScore * w.VcpQuality +
            score.RevenueAccelerationScore * w.RevenueAcceleration +
            score.TrendStructureScore * w.TrendStructure +
            score.InstitutionalActivityScore * w.InstitutionalActivity +
            score.SectorScore * w.SectorStrength
        ) / 100m;

        AssertNear(expected, score.TotalScore, 0.001m, "TotalScore vs manual weighted sum");
    }

    [Fact]
    public void MissingFundamentals_ScoreZero_NotFabricatedPositive()
    {
        var inputs = BuildBaselineInputs(); // CurrentFundamentals/PriorFundamentals both null
        var score = StockScoringEngine.Calculate(inputs, new StrategyOptions(), new StockScoringOptions());

        Assert.Equal(0m, score.EarningsAccelerationScore);
        Assert.Equal(0m, score.RevenueAccelerationScore);
        Assert.Equal(0m, score.InstitutionalActivityScore);
    }

    [Fact]
    public void MissingFundamentals_DataQualityScore_ReflectsIncompleteness()
    {
        var inputs = BuildBaselineInputs();
        var strategyOptions = new StrategyOptions();
        var score = StockScoringEngine.Calculate(inputs, strategyOptions, new StockScoringOptions());

        var w = strategyOptions.Weights;
        var totalWeight = w.RelativeStrength + w.VolumeAccumulation + w.PriceMomentum + w.EarningsAcceleration +
                           w.VcpQuality + w.RevenueAcceleration + w.TrendStructure + w.InstitutionalActivity + w.SectorStrength;
        var missingWeight = w.EarningsAcceleration + w.RevenueAcceleration + w.InstitutionalActivity;
        var expectedDataQuality = 100m * (totalWeight - missingWeight) / totalWeight;

        AssertNear(expectedDataQuality, score.DataQualityScore, 0.01m, "DataQualityScore");
        Assert.True(score.DataQualityScore < 100m, "DataQualityScore should be below 100 when fundamentals are missing.");
    }

    [Fact]
    public void CompleteFundamentalsAndInstitutionalData_DataQualityScoreIsOneHundred()
    {
        var inputs = BuildBaselineInputs();
        inputs = new StockScoringInputs
        {
            Symbol = inputs.Symbol, AsOfDate = inputs.AsOfDate, Bars = inputs.Bars, IndicatorSnapshots = inputs.IndicatorSnapshots,
            VcpPattern = inputs.VcpPattern, RelativeStrengthScore = inputs.RelativeStrengthScore, SectorStrengthScore = inputs.SectorStrengthScore,
            CurrentFundamentals = new FundamentalSnapshot { Symbol = "STK", AsOfDate = inputs.AsOfDate, EarningsGrowthPercent = 20m, EarningsGrowthAvailability = DataAvailability.Available, RevenueGrowthPercent = 15m, RevenueGrowthAvailability = DataAvailability.Available },
            PriorFundamentals = new FundamentalSnapshot { Symbol = "STK", AsOfDate = inputs.AsOfDate.AddMonths(-3), EarningsGrowthPercent = 12m, EarningsGrowthAvailability = DataAvailability.Available, RevenueGrowthPercent = 10m, RevenueGrowthAvailability = DataAvailability.Available },
            InstitutionalActivity = new InstitutionalActivitySnapshot { Symbol = "STK", AsOfDate = inputs.AsOfDate, ActivityScore = 65m, Availability = DataAvailability.Available },
        };

        var score = StockScoringEngine.Calculate(inputs, new StrategyOptions(), new StockScoringOptions());
        AssertNear(100m, score.DataQualityScore, 0.01m, "DataQualityScore with everything available");
    }

    [Fact]
    public void NoVcpPattern_ScoresZero_ButCountsAsAvailableData()
    {
        // VcpPattern == null ("no pattern currently forming") is a real
        // assessment, not missing data — DataQualityScore should NOT be
        // reduced on its account.
        var withVcp = BuildBaselineInputs();
        var noVcpScore = StockScoringEngine.Calculate(withVcp, new StrategyOptions(), new StockScoringOptions());

        Assert.Equal(0m, noVcpScore.VcpScore);

        // Compare against a case with genuinely missing fundamentals only (same as baseline) —
        // VcpQuality's weight should be counted as "available" either way.
        var strategyOptions = new StrategyOptions();
        var w = strategyOptions.Weights;
        var totalWeight = w.RelativeStrength + w.VolumeAccumulation + w.PriceMomentum + w.EarningsAcceleration +
                           w.VcpQuality + w.RevenueAcceleration + w.TrendStructure + w.InstitutionalActivity + w.SectorStrength;
        var missingWeight = w.EarningsAcceleration + w.RevenueAcceleration + w.InstitutionalActivity; // VcpQuality NOT included
        var expectedDataQuality = 100m * (totalWeight - missingWeight) / totalWeight;

        AssertNear(expectedDataQuality, noVcpScore.DataQualityScore, 0.01m, "VcpQuality weight should count as available even with no pattern");
    }

    [Fact]
    public void UnknownSector_ScoresZero_DataQualityReducedAccordingly()
    {
        var inputs = BuildBaselineInputs();
        var withUnknownSector = new StockScoringInputs
        {
            Symbol = inputs.Symbol, AsOfDate = inputs.AsOfDate, Bars = inputs.Bars, IndicatorSnapshots = inputs.IndicatorSnapshots,
            VcpPattern = inputs.VcpPattern, RelativeStrengthScore = inputs.RelativeStrengthScore, SectorStrengthScore = null, // Unknown sector
        };

        var score = StockScoringEngine.Calculate(withUnknownSector, new StrategyOptions(), new StockScoringOptions());
        Assert.Equal(0m, score.SectorScore);
    }

    [Fact]
    public void EmptyBars_Throws()
    {
        var inputs = BuildBaselineInputs();
        var empty = new StockScoringInputs
        {
            Symbol = inputs.Symbol, AsOfDate = inputs.AsOfDate,
            Bars = Array.Empty<HistoricalBar>(), IndicatorSnapshots = Array.Empty<IndicatorSnapshot>(),
            RelativeStrengthScore = 50m,
        };

        Assert.Throws<ArgumentException>(() => StockScoringEngine.Calculate(empty, new StrategyOptions(), new StockScoringOptions()));
    }

    private static void AssertNear(decimal expected, decimal actual, decimal tolerance, string label)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance, $"{label}: expected {expected}, got {actual} (diff {diff} > tolerance {tolerance})");
    }
}
