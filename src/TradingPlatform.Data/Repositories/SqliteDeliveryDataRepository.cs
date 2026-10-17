using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteDeliveryDataRepository : IDeliveryDataRepository
{
    private readonly SqliteConnection _connection;

    public SqliteDeliveryDataRepository(SqliteConnection connection) => _connection = connection;

    public Task UpsertRangeAsync(IReadOnlyList<DeliveryRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0) return Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        using var tx = _connection.BeginTransaction();
        foreach (var r in records)
        {
            _connection.Execute(
                """
                INSERT INTO DeliveryData (Symbol, TradeDate, TotalTradedQuantity, DeliverableQuantity, DeliveryPercent, Source, CreatedAt)
                VALUES (?,?,?,?,?,?,?)
                ON CONFLICT(Symbol, TradeDate) DO UPDATE SET
                    TotalTradedQuantity = excluded.TotalTradedQuantity, DeliverableQuantity = excluded.DeliverableQuantity,
                    DeliveryPercent = excluded.DeliveryPercent, Source = excluded.Source, CreatedAt = excluded.CreatedAt;
                """,
                r.Symbol.ToUpperInvariant(), r.TradeDate, r.TotalTradedQuantity, r.DeliverableQuantity, r.DeliveryPercent, r.Source, now);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeliveryRecord>> GetRangeAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.Query(
            "SELECT * FROM DeliveryData WHERE Symbol = ? AND TradeDate >= ? AND TradeDate <= ? ORDER BY TradeDate;", Map, symbol.ToUpperInvariant(), from, to));

    public Task<DateOnly?> GetLatestDateAsync(CancellationToken cancellationToken = default)
    {
        var text = _connection.QuerySingleOrDefault("SELECT MAX(TradeDate) AS d FROM DeliveryData;", r => r.GetNullableString("d") ?? "");
        return Task.FromResult(string.IsNullOrEmpty(text) ? (DateOnly?)null : DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DeliveryRecord Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        TradeDate = row.GetDateOnly("TradeDate"),
        TotalTradedQuantity = row.GetNullableInt64("TotalTradedQuantity"),
        DeliverableQuantity = row.GetNullableInt64("DeliverableQuantity"),
        DeliveryPercent = row.GetDecimal("DeliveryPercent"),
        Source = row.GetString("Source"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
