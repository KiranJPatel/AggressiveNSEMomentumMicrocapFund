using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteHistoricalBarRepository : IHistoricalBarRepository
{
    private readonly SqliteConnection _connection;

    public SqliteHistoricalBarRepository(SqliteConnection connection) => _connection = connection;

    public Task<IReadOnlyList<BarCoverage>> GetCoverageAsync(Timeframe timeframe, CancellationToken cancellationToken = default)
    {
        // Timestamps are stored as ISO-8601 text in one consistent offset, so MIN/MAX on the text are the earliest/latest bars.
        var rows = _connection.Query(
            "SELECT Symbol, MIN(Timestamp) AS FirstTs, MAX(Timestamp) AS LastTs, COUNT(*) AS N FROM HistoricalBars WHERE Timeframe = ? GROUP BY Symbol ORDER BY Symbol;",
            r => new BarCoverage(r.GetString("Symbol"), DateOnly.FromDateTime(r.GetDateTimeOffset("FirstTs").DateTime), DateOnly.FromDateTime(r.GetDateTimeOffset("LastTs").DateTime), r.GetInt32("N")),
            timeframe);
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<HistoricalBar>> GetRangeAsync(string symbol, DateOnly from, DateOnly to, Timeframe timeframe, CancellationToken cancellationToken = default)
    {
        var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toTs = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        var result = _connection.Query(
            """
            SELECT * FROM HistoricalBars
            WHERE Symbol = ? AND Timeframe = ? AND Timestamp >= ? AND Timestamp <= ?
            ORDER BY Timestamp;
            """,
            Map, symbol, timeframe, fromTs, toTs);

        return Task.FromResult(result);
    }

    public Task UpsertRangeAsync(IReadOnlyList<HistoricalBar> bars, CancellationToken cancellationToken = default)
    {
        if (bars.Count == 0) return Task.CompletedTask;

        using var tx = _connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        foreach (var bar in bars)
        {
            // INSERT OR REPLACE keyed on the (Symbol, Timestamp, Timeframe) unique index —
            // safe to call repeatedly with overlapping date ranges (spec §5.2).
            _connection.Execute(
                """
                INSERT INTO HistoricalBars
                    (Symbol, Timestamp, [Open], High, Low, [Close], AdjustedClose, Volume, Source, Timeframe, CreatedAt)
                VALUES (?,?,?,?,?,?,?,?,?,?,?)
                ON CONFLICT(Symbol, Timestamp, Timeframe) DO UPDATE SET
                    [Open]=excluded.[Open], High=excluded.High, Low=excluded.Low, [Close]=excluded.[Close],
                    AdjustedClose=excluded.AdjustedClose, Volume=excluded.Volume, Source=excluded.Source;
                """,
                bar.Symbol, bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.AdjustedClose,
                bar.Volume, bar.Source, bar.Timeframe, now);
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    private static HistoricalBar Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        Timestamp = row.GetDateTimeOffset("Timestamp"),
        Open = row.GetDecimal("Open"),
        High = row.GetDecimal("High"),
        Low = row.GetDecimal("Low"),
        Close = row.GetDecimal("Close"),
        AdjustedClose = row.GetDecimal("AdjustedClose"),
        Volume = row.GetInt64("Volume"),
        Source = row.GetString("Source"),
        Timeframe = row.GetEnum<Timeframe>("Timeframe"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
