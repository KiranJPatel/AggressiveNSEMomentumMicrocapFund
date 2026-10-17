using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteScannerResultRepository : IScannerResultRepository
{
    private readonly SqliteConnection _connection;

    public SqliteScannerResultRepository(SqliteConnection connection) => _connection = connection;

    public Task InsertCycleAsync(IReadOnlyList<ScannerResult> results, CancellationToken cancellationToken = default)
    {
        using var tx = _connection.BeginTransaction();

        foreach (var r in results)
        {
            _connection.ExecuteInsert(
                """
                INSERT INTO ScannerResults
                    (ScanTimestamp, Rank, Symbol, Price, MarketCap, Sector, Score, RelativeStrengthScore,
                     MomentumScore, VolumeScore, VcpScore, Adx, Rsi, Atr, PivotPrice, BreakoutStatus,
                     DistanceFromPivotPercent, SuggestedEntry, SuggestedStop, Risk, SuggestedPositionSize)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
                """,
                r.ScanTimestamp, r.Rank, r.Symbol, r.Price, r.MarketCap, r.Sector, r.Score,
                r.RelativeStrengthScore, r.MomentumScore, r.VolumeScore, r.VcpScore, r.Adx, r.Rsi, r.Atr,
                r.PivotPrice, r.BreakoutStatus, r.DistanceFromPivotPercent, r.SuggestedEntry, r.SuggestedStop,
                r.Risk, r.SuggestedPositionSize);
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScannerResult>> GetLatestCycleAsync(CancellationToken cancellationToken = default)
    {
        // Two queries rather than a subquery-in-WHERE for clarity — this
        // table is small (one scan cycle's worth of rows read at a time,
        // not the whole history) so the extra round trip is immaterial.
        //
        // Uses Query<T> (not TryQuerySingleValue<T>) deliberately: T here is
        // DateTimeOffset?, and TryQuerySingleValue's `where T : struct`
        // constraint cannot be satisfied by a Nullable<T> type argument
        // (CS0453 — C# excludes Nullable<T> from `struct` constraints to
        // avoid Nullable<Nullable<T>>). A MAX(...) aggregate with no
        // matching rows still returns exactly one row with a NULL value,
        // never zero rows, so reading row 0 here is always safe.
        var latestRows = _connection.Query(
            "SELECT MAX(ScanTimestamp) AS LatestTs FROM ScannerResults;",
            row => row.IsDBNull("LatestTs") ? (DateTimeOffset?)null : row.GetDateTimeOffset("LatestTs"));

        if (latestRows.Count == 0 || latestRows[0] is not { } latest)
        {
            return Task.FromResult<IReadOnlyList<ScannerResult>>(Array.Empty<ScannerResult>());
        }

        var result = _connection.Query(
            "SELECT * FROM ScannerResults WHERE ScanTimestamp = ? ORDER BY Rank;", Map, latest);
        return Task.FromResult(result);
    }

    private static ScannerResult Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        ScanTimestamp = row.GetDateTimeOffset("ScanTimestamp"),
        Rank = row.GetInt32("Rank"),
        Symbol = row.GetString("Symbol"),
        Price = row.GetDecimal("Price"),
        MarketCap = row.GetNullableDecimal("MarketCap"),
        Sector = row.GetNullableString("Sector"),
        Score = row.GetDecimal("Score"),
        RelativeStrengthScore = row.GetDecimal("RelativeStrengthScore"),
        MomentumScore = row.GetDecimal("MomentumScore"),
        VolumeScore = row.GetDecimal("VolumeScore"),
        VcpScore = row.GetDecimal("VcpScore"),
        Adx = row.GetNullableDecimal("Adx"),
        Rsi = row.GetNullableDecimal("Rsi"),
        Atr = row.GetNullableDecimal("Atr"),
        PivotPrice = row.GetDecimal("PivotPrice"),
        BreakoutStatus = row.GetString("BreakoutStatus"),
        DistanceFromPivotPercent = row.GetDecimal("DistanceFromPivotPercent"),
        SuggestedEntry = row.GetDecimal("SuggestedEntry"),
        SuggestedStop = row.GetDecimal("SuggestedStop"),
        Risk = row.GetDecimal("Risk"),
        SuggestedPositionSize = row.GetDecimal("SuggestedPositionSize"),
    };
}
