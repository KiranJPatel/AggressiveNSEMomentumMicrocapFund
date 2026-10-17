using TradingPlatform.Data.Sqlite;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

/// <summary>
/// Exercises the hand-rolled P/Invoke SQLite wrapper directly (no schema,
/// no repositories) — the foundation everything else in Tranche 1 sits on.
/// Each test opens its own throwaway file-backed database (SQLite's
/// ":memory:" is a single connection's private DB, which is fine here
/// since each test uses exactly one connection).
/// </summary>
public class SqliteConnectionTests
{
    private static SqliteConnection OpenTempDb() => new(Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db"));

    [Fact]
    public void CreateTable_Insert_And_Query_RoundTrips()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        var id = db.ExecuteInsert("INSERT INTO t (name) VALUES (?);", "hello world");

        Assert.True(id > 0);

        var rows = db.Query("SELECT * FROM t WHERE id = ?;", row => row.GetString("name"), id);
        Assert.Equal(1, rows.Count);
        Assert.Equal("hello world", rows[0]);
    }

    [Fact]
    public void DecimalValue_RoundTrips_WithoutPrecisionLoss()
    {
        // The whole reason decimal is bound/read as TEXT and never SQLite's
        // native REAL (double): this exact value is NOT exactly representable
        // in IEEE-754 double, so if it round-trips exactly, the TEXT path is proven.
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, price TEXT NOT NULL);");
        var tricky = 123456.789012345678m;
        var id = db.ExecuteInsert("INSERT INTO t (price) VALUES (?);", tricky);

        var result = db.Query("SELECT * FROM t WHERE id = ?;", row => row.GetDecimal("price"), id);
        Assert.Equal(tricky, result[0]);
    }

    [Fact]
    public void NullParameter_BindsAndReadsAsNull()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, note TEXT NULL);");
        var id = db.ExecuteInsert("INSERT INTO t (note) VALUES (?);", (string?)null);

        var isNull = db.Query("SELECT * FROM t WHERE id = ?;", row => row.IsDBNull("note"), id);
        Assert.True(isNull[0]);
    }

    [Fact]
    public void UniqueConstraint_RejectsDuplicateInsert()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, symbol TEXT NOT NULL UNIQUE);");
        db.ExecuteInsert("INSERT INTO t (symbol) VALUES (?);", "RELIANCE");

        Assert.Throws<SqliteException>(() => db.ExecuteInsert("INSERT INTO t (symbol) VALUES (?);", "RELIANCE"));
    }

    [Fact]
    public void Transaction_RollsBackOnDisposeWithoutCommit()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");

        using (var tx = db.BeginTransaction())
        {
            db.ExecuteInsert("INSERT INTO t (name) VALUES (?);", "should not survive");
            // no Commit() — Dispose() must roll back
        }

        var count = db.Query("SELECT COUNT(*) as c FROM t;", row => row.GetInt64("c"));
        Assert.Equal(0L, count[0]);
    }

    [Fact]
    public void Transaction_PersistsOnCommit()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");

        using (var tx = db.BeginTransaction())
        {
            db.ExecuteInsert("INSERT INTO t (name) VALUES (?);", "should survive");
            tx.Commit();
        }

        var count = db.Query("SELECT COUNT(*) as c FROM t;", row => row.GetInt64("c"));
        Assert.Equal(1L, count[0]);
    }

    [Fact]
    public void DateTimeOffset_RoundTrips_WithOffsetPreserved()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, ts TEXT NOT NULL);");
        var original = new DateTimeOffset(2026, 3, 15, 9, 15, 0, TimeSpan.FromHours(5.5)); // IST
        var id = db.ExecuteInsert("INSERT INTO t (ts) VALUES (?);", original);

        var result = db.Query("SELECT * FROM t WHERE id = ?;", row => row.GetDateTimeOffset("ts"), id);
        Assert.Equal(original, result[0]);
        Assert.Equal(original.Offset, result[0].Offset);
    }

    [Fact]
    public void UnicodeText_RoundTrips_Correctly()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        const string original = "Reliance Industries Ltd. — ₹ प्रतीक";
        var id = db.ExecuteInsert("INSERT INTO t (name) VALUES (?);", original);

        var result = db.Query("SELECT * FROM t WHERE id = ?;", row => row.GetString("name"), id);
        Assert.Equal(original, result[0]);
    }

    [Fact]
    public void TryQuerySingleValue_NoMatchingRows_ReturnsFalse()
    {
        // Regression guard for why TryQuerySingleValue exists at all:
        // an unconstrained "T?" does NOT become Nullable<T> for a value-type
        // T (only "where T : struct" does that) — it silently erases to
        // plain T, so a single generic QuerySingleOrDefault<T> could never
        // distinguish "no rows" from "a row containing 0" for T=long. See
        // SqliteConnection.QuerySingleOrDefault's XML doc and docs/gap-log.md.
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, val INTEGER NOT NULL);");

        var found = db.TryQuerySingleValue("SELECT val FROM t WHERE id = ?;", row => row.GetInt64("val"), out var value, 999);

        Assert.False(found);
        Assert.Equal(0L, value); // default(long) — caller must check the bool, not the value
    }

    [Fact]
    public void TryQuerySingleValue_RowWithZeroValue_ReturnsTrue_WithZero()
    {
        // The other half of the same guard: a genuine row containing 0 must
        // still report found=true, not be confused with "no rows".
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, val INTEGER NOT NULL);");
        db.ExecuteInsert("INSERT INTO t (id, val) VALUES (1, 0);");

        var found = db.TryQuerySingleValue("SELECT val FROM t WHERE id = ?;", row => row.GetInt64("val"), out var value, 1);

        Assert.True(found, "Expected a real row (val=0) to report found=true.");
        Assert.Equal(0L, value);
    }

    [Fact]
    public void TryQuerySingleValue_MoreThanOneRow_Throws()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, grp INTEGER NOT NULL);");
        db.ExecuteInsert("INSERT INTO t (grp) VALUES (1);");
        db.ExecuteInsert("INSERT INTO t (grp) VALUES (1);");

        Assert.Throws<InvalidOperationException>(() =>
            db.TryQuerySingleValue("SELECT id FROM t WHERE grp = ?;", row => row.GetInt64("id"), out _, 1));
    }

    [Fact]
    public void QuerySingleOrDefault_ReferenceTypeResult_NoMatchingRows_ReturnsNull()
    {
        using var db = OpenTempDb();
        db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");

        var result = db.QuerySingleOrDefault("SELECT name FROM t WHERE id = ?;", row => row.GetString("name"), 999);

        Assert.True(result is null);
    }

    [Fact]
    public void FileDatabase_UsesWal_AndEnforcesForeignKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db");
        using var db = new SqliteConnection(path);

        var journal = db.Query("PRAGMA journal_mode;", r => r.GetString("journal_mode")).Single();
        var fk = db.Query("PRAGMA foreign_keys;", r => r.GetInt32("foreign_keys")).Single();

        Assert.Equal("wal", journal);
        Assert.Equal(1, fk);
    }

    [Fact]
    public void ForeignKeyViolation_IsRejected_InsteadOfSilentlyStoringAnOrphan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tp_test_{Guid.NewGuid():N}.db");
        using var db = new SqliteConnection(path);
        TradingPlatform.Data.Schema.SchemaInitializer.Initialize(db);

        Assert.Throws<SqliteException>(() => db.Execute(
            "INSERT INTO UniverseClassificationSnapshots (InstrumentId, Symbol, MarketCapBucket, EffectiveFrom, IsUnknown, CreatedAt) VALUES (999,'X',1,'2026-01-01',0,'x');"));
    }
}
