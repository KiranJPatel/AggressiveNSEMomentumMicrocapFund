using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Repositories;
using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class RegimeAndOptimizationRepositoryTests
{
    private static SqliteConnection Db()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        SchemaInitializer.Initialize(db);
        return db;
    }

    private static long Count(SqliteConnection db, string table)
    {
        db.TryQuerySingleValue($"SELECT COUNT(*) AS c FROM {table};", r => Convert.ToInt64(r.GetInt64("c")), out var n);
        return n;
    }

    private static MarketRegimeSnapshot Snap(DateOnly d, int score, decimal? breadth50) => new()
    {
        AsOfDate = d, RegimeScore = score, Classification = RegimeClassification.Aggressive, DeployableCapitalPercent = 100m,
        NiftyTrendScore = 90m, BreadthScore = 70m, AdvanceDeclineRatio = 1.8m, PercentUniverseAbove50Dma = breadth50,
        PercentUniverseAbove200Dma = 55m, SmallMidRelativeStrength = 60m, VolatilityScore = 80m, BreakoutSuccessRate = null,
    };

    [Fact]
    public async Task MarketRegime_Upsert_SameDayUpdatesInPlace_AndNullsRoundTrip()
    {
        using var db = Db();
        var repo = new SqliteMarketRegimeRepository(db);
        var day = new DateOnly(2026, 3, 2);

        await repo.UpsertAsync(Snap(day, 71, 64.5m));
        await repo.UpsertAsync(Snap(day, 83, null));       // re-run for the same day replaces, not duplicates

        var range = await repo.GetRangeAsync(day, day);
        var row = Assert.Single(range);
        Assert.Equal(83, row.RegimeScore);
        Assert.Null(row.PercentUniverseAbove50Dma);
        Assert.Null(row.BreakoutSuccessRate);
        Assert.Equal(1.8m, row.AdvanceDeclineRatio!.Value);
        Assert.Equal(RegimeClassification.Aggressive, row.Classification);
    }

    [Fact]
    public async Task MarketRegime_GetLatest_ReturnsNewestDate()
    {
        using var db = Db();
        var repo = new SqliteMarketRegimeRepository(db);
        await repo.UpsertAsync(Snap(new DateOnly(2026, 3, 2), 60, 50m));
        await repo.UpsertAsync(Snap(new DateOnly(2026, 3, 4), 40, 30m));
        await repo.UpsertAsync(Snap(new DateOnly(2026, 3, 3), 50, 40m));

        Assert.Equal(new DateOnly(2026, 3, 4), (await repo.GetLatestAsync())!.AsOfDate);
    }

    [Fact]
    public async Task OptimizationRuns_InsertBatch_GroupsUnderOneId_AndReadsBackBestFirst()
    {
        using var db = Db();
        var repo = new SqliteOptimizationRunRepository(db);
        var rows = new[]
        {
            new OptimizationRunRecord { ParameterSetJson = "{\"a\":1}", CompositeScoreBreakdownJson = "{}", CompositeScore = 12.5m, IsOutOfSample = true },
            new OptimizationRunRecord { ParameterSetJson = "{\"a\":2}", CompositeScoreBreakdownJson = "{}", CompositeScore = 40m, IsOutOfSample = true },
            new OptimizationRunRecord { ParameterSetJson = "{\"a\":3}", CompositeScoreBreakdownJson = "{}", CompositeScore = -3m, IsOutOfSample = true },
        };

        var batch = await repo.InsertBatchAsync(rows);
        var other = await repo.InsertBatchAsync(new[] { rows[0] });

        var read = await repo.GetBatchAsync(batch);
        Assert.Equal(3, read.Count);
        Assert.Equal(40m, read[0].CompositeScore);
        Assert.Equal(-3m, read[2].CompositeScore);
        Assert.NotEqual(batch, other);
        Assert.Equal(1, (await repo.GetBatchAsync(other)).Count);
    }

    [Fact]
    public void SchemaInitializer_AddsBatchIdToAnOptimizationRunsTableFromAnOlderDatabase()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        db.Execute("CREATE TABLE OptimizationRuns (Id INTEGER PRIMARY KEY AUTOINCREMENT, ParameterSetJson TEXT NOT NULL, CompositeScoreBreakdownJson TEXT NOT NULL, CompositeScore TEXT NOT NULL, IsOutOfSample INTEGER NOT NULL, CreatedAt TEXT NOT NULL);");

        SchemaInitializer.Initialize(db);

        db.Execute("INSERT INTO OptimizationRuns (BatchId, ParameterSetJson, CompositeScoreBreakdownJson, CompositeScore, IsOutOfSample, CreatedAt) VALUES ('b','{}','{}','1',1,'x');");
        db.Dispose();
    }

    [Fact]
    public async Task ScanEvidence_Upsert_WritesAllThreeTables_AndRescansUpdateInPlace()
    {
        using var db = Db();
        var repo = new SqliteScanEvidenceRepository(db);
        var day = new DateOnly(2026, 3, 2);
        var ts = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(5.5));
        ScanEvidence Make(decimal total, decimal? rsi) => new("ABC",
            new StockScore { Symbol = "ABC", AsOfDate = day, TotalScore = total, TechnicalScore = 40m, FundamentalScore = 30m, RelativeStrengthScore = 15m, VolumeScore = 10m, PriceMomentumScore = 9m, EarningsAccelerationScore = 8m, VcpScore = 7m, RevenueAccelerationScore = 6m, TrendStructureScore = 4m, InstitutionalActivityScore = 3m, SectorScore = 2m, DataQualityScore = 95m },
            new VcpPattern { Symbol = "ABC", AsOfDate = day, VcpScore = 81m, NumberOfContractions = 3, ContractionRatios = new[] { 0.7m, 0.6m }, PivotPrice = 123.45m, BaseLengthDays = 40, AverageVolumeDuringBase = 150000m, VolumeDryUpScore = 70m },
            new IndicatorSnapshot { Symbol = "ABC", Timestamp = ts, Timeframe = Timeframe.Daily, Rsi = rsi, Adx = 27.5m, HmaFast = 101m });

        await repo.UpsertAsync(new[] { Make(70m, 61m) });
        await repo.UpsertAsync(new[] { Make(77m, null) });   // same session rescanned

        var score = await repo.GetLatestScoreAsync("ABC");
        var vcp = await repo.GetLatestVcpAsync("ABC");
        var ind = await repo.GetLatestIndicatorsAsync("ABC");
        Assert.Equal(77m, score!.TotalScore);
        Assert.Equal(15m, score.RelativeStrengthScore);
        Assert.Equal(2, vcp!.ContractionRatios.Count);
        Assert.Equal(123.45m, vcp.PivotPrice);
        Assert.Null(ind!.Rsi);
        Assert.Equal(27.5m, ind.Adx!.Value);
        Assert.Equal(1L, Count(db, "StockScores"));
        Assert.Equal(1L, Count(db, "Indicators"));
    }

    [Fact]
    public async Task ScanEvidence_WithoutAVcpPattern_StillStoresScoreAndIndicators()
    {
        using var db = Db();
        var repo = new SqliteScanEvidenceRepository(db);
        var day = new DateOnly(2026, 3, 2);
        var e = new ScanEvidence("XYZ",
            new StockScore { Symbol = "XYZ", AsOfDate = day, TotalScore = 50m },
            null,
            new IndicatorSnapshot { Symbol = "XYZ", Timestamp = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), Timeframe = Timeframe.Daily });

        await repo.UpsertAsync(new[] { e });

        Assert.NotNull(await repo.GetLatestScoreAsync("XYZ"));
        Assert.Null(await repo.GetLatestVcpAsync("XYZ"));
        Assert.NotNull(await repo.GetLatestIndicatorsAsync("XYZ"));
    }

    [Fact]
    public async Task MarketRegime_WeeklyBias_RoundTrips_AndOldRowsReadAsNull()
    {
        using var db = Db();
        var repo = new SqliteMarketRegimeRepository(db);
        var withBias = Snap(new DateOnly(2026, 3, 2), 70, 50m); withBias.WeeklyBias = WeeklyBias.Bearish;
        var without = Snap(new DateOnly(2026, 3, 3), 70, 50m);   // WeeklyBias left null, as in a row written before the column existed

        await repo.UpsertAsync(withBias);
        await repo.UpsertAsync(without);

        var rows = await repo.GetRangeAsync(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3));
        Assert.Equal(WeeklyBias.Bearish, rows[0].WeeklyBias);
        Assert.Null(rows[1].WeeklyBias);
    }

    [Fact]
    public void SchemaInitializer_AddsWeeklyBiasToAnOlderMarketRegimesTable()
    {
        var db = new SqliteConnection(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));
        db.Execute("CREATE TABLE MarketRegimes (Id INTEGER PRIMARY KEY AUTOINCREMENT, AsOfDate TEXT NOT NULL UNIQUE, RegimeScore INTEGER NOT NULL, Classification INTEGER NOT NULL, DeployableCapitalPercent TEXT NOT NULL, NiftyTrendScore TEXT NULL, BreadthScore TEXT NULL, AdvanceDeclineRatio TEXT NULL, PercentUniverseAbove50Dma TEXT NULL, PercentUniverseAbove200Dma TEXT NULL, SmallMidRelativeStrength TEXT NULL, VolatilityScore TEXT NULL, BreakoutSuccessRate TEXT NULL, CreatedAt TEXT NOT NULL);");

        SchemaInitializer.Initialize(db);

        db.Execute("UPDATE MarketRegimes SET WeeklyBias = 1;");
        db.Dispose();
    }
}
