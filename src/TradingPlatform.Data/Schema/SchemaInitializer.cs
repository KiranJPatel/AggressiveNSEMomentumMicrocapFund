using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Schema;

/// <summary>
/// Applies SchemaDefinition.Statements idempotently and records the applied
/// version in SchemaVersion. Safe to call on every startup (spec §7.2).
/// A single flat statement list is sufficient for now (version 1); once a
/// second version exists this becomes a proper ordered-migration runner.
/// </summary>
public static class SchemaInitializer
{
    public static void Initialize(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();

        foreach (var statement in SchemaDefinition.Statements)
            connection.Execute(statement);

        // Additive column migration (Tranche 18): PortfolioSnapshot.Regime was
        // added to the Core model in Tranche 16 for BacktestRunResult's
        // in-memory DailySnapshots (never persisted to SQLite at that point,
        // so the gap was invisible then) but the live PortfolioSnapshots
        // table was never given a matching column. CREATE TABLE IF NOT
        // EXISTS only helps a brand-new database; a database created by an
        // earlier tranche's zip needs the column added explicitly. SQLite
        // has no "ADD COLUMN IF NOT EXISTS", so this checks PRAGMA
        // table_info first — safe to run on every startup.
        EnsureColumn(connection, "PortfolioSnapshots", "Regime", "INTEGER NULL");
        // Optimization batches: groups the candidates of one `optimize` invocation (older databases have the table without it).
        EnsureColumn(connection, "OptimizationRuns", "BatchId", "TEXT NULL");
        // Weekly market bias (spec §7.3) added to MarketRegimes after the table first shipped.
        EnsureColumn(connection, "MarketRegimes", "WeeklyBias", "INTEGER NULL");

        var alreadyApplied = connection.Query(
            "SELECT Version FROM SchemaVersion WHERE Version = ?;",
            row => row.GetInt32("Version"),
            SchemaDefinition.CurrentVersion);

        if (alreadyApplied.Count == 0)
        {
            connection.Execute(
                "INSERT INTO SchemaVersion (Version, AppliedAt) VALUES (?, ?);",
                SchemaDefinition.CurrentVersion,
                DateTimeOffset.UtcNow);
        }

        tx.Commit();
    }

    /// <summary>Adds `column` to `table` if it isn't already there (checked via PRAGMA table_info, since SQLite has no "ADD COLUMN IF NOT EXISTS"). See the Tranche 18 remarks above Initialize's call site for why this exists.</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string columnDefinitionSql)
    {
        var existingColumns = connection.Query(
            $"PRAGMA table_info({table});",
            row => row.GetString("name"));

        if (!existingColumns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
            connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {columnDefinitionSql};");
    }
}
