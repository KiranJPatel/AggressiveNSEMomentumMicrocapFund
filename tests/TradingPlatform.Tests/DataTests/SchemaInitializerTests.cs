using TradingPlatform.Data.Schema;
using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class SchemaInitializerTests
{
    private static SqliteConnection OpenTempDb() => new(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));

    [Fact]
    public void Initialize_CreatesAllExpectedTables()
    {
        using var db = OpenTempDb();
        SchemaInitializer.Initialize(db);

        var tables = db.Query(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;",
            row => row.GetString("name"));

        string[] expected =
        {
            "BacktestRuns", "BacktestTrades", "DrawdownEvents", "Fills", "FundamentalSnapshots",
            "HistoricalBars", "Indicators", "InstitutionalActivitySnapshots", "Instruments",
            "DeliveryData", "MarketRegimes", "OptimizationRuns", "OrderEvents", "Orders", "PortfolioSnapshots",
            "Positions", "RiskEvents", "ScannerResults", "SchemaVersion", "Signals",
            "StockScores", "SurveillanceStatuses", "SystemEvents", "TradeLegs", "Trades",
            "UniverseClassificationSnapshots", "VcpPatterns"
        };

        foreach (var table in expected)
            Assert.True(tables.Contains(table), $"Expected table '{table}' to exist after schema initialization.");
    }

    [Fact]
    public void Initialize_IsIdempotent_RunningTwiceDoesNotDuplicateSchemaVersionRow()
    {
        using var db = OpenTempDb();
        SchemaInitializer.Initialize(db);
        SchemaInitializer.Initialize(db);
        SchemaInitializer.Initialize(db);

        var count = db.Query("SELECT COUNT(*) as c FROM SchemaVersion;", row => row.GetInt64("c"));
        Assert.Equal(1L, count[0]);
    }

    [Fact]
    public void Initialize_RecordsCurrentSchemaVersion()
    {
        using var db = OpenTempDb();
        SchemaInitializer.Initialize(db);

        var version = db.Query("SELECT Version FROM SchemaVersion;", row => row.GetInt32("Version"));
        Assert.Equal(SchemaDefinition.CurrentVersion, version[0]);
    }

    [Fact]
    public void HistoricalBars_DuplicateSymbolTimestampTimeframe_IsRejectedByUniqueIndex()
    {
        using var db = OpenTempDb();
        SchemaInitializer.Initialize(db);

        var ts = DateTimeOffset.UtcNow;
        db.ExecuteInsert(
            "INSERT INTO HistoricalBars (Symbol, Timestamp, [Open], High, Low, [Close], AdjustedClose, Volume, Source, Timeframe, CreatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?);",
            "RELIANCE", ts, 100m, 105m, 99m, 104m, 104m, 1000L, "Yahoo", 1, ts);

        Assert.Throws<SqliteException>(() => db.ExecuteInsert(
            "INSERT INTO HistoricalBars (Symbol, Timestamp, [Open], High, Low, [Close], AdjustedClose, Volume, Source, Timeframe, CreatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?);",
            "RELIANCE", ts, 100m, 105m, 99m, 104m, 104m, 1000L, "Yahoo", 1, ts));
    }
}
