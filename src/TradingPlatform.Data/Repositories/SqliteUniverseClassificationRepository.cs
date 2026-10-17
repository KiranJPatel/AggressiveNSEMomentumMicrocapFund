using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteUniverseClassificationRepository : IUniverseClassificationRepository
{
    private readonly SqliteConnection _connection;

    public SqliteUniverseClassificationRepository(SqliteConnection connection) => _connection = connection;

    public Task<IReadOnlyList<UniverseClassificationSnapshot>> GetHistoryAsync(string symbol, CancellationToken cancellationToken = default) =>
        Task.FromResult(_connection.Query("SELECT * FROM UniverseClassificationSnapshots WHERE Symbol = ? ORDER BY EffectiveFrom DESC;", Map, symbol));

    public Task<UniverseClassificationSnapshot?> GetClassificationAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        // The snapshot effective on asOfDate: EffectiveFrom <= asOfDate AND (EffectiveTo IS NULL OR EffectiveTo >= asOfDate).
        // Never "most recent regardless of date" — see spec §2.2 on point-in-time correctness for backtests.
        var result = _connection.QuerySingleOrDefault(
            """
            SELECT * FROM UniverseClassificationSnapshots
            WHERE Symbol = ? AND EffectiveFrom <= ? AND (EffectiveTo IS NULL OR EffectiveTo >= ?)
            ORDER BY EffectiveFrom DESC
            LIMIT 1;
            """,
            Map, symbol, asOfDate, asOfDate);

        return Task.FromResult(result);
    }

    public Task UpsertAsync(UniverseClassificationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _connection.ExecuteInsert(
            """
            INSERT INTO UniverseClassificationSnapshots
                (InstrumentId, Symbol, MarketCapBucket, MarketCapRank, Sector, EffectiveFrom, EffectiveTo, IsUnknown, CreatedAt)
            VALUES (?,?,?,?,?,?,?,?,?);
            """,
            snapshot.InstrumentId, snapshot.Symbol, snapshot.MarketCapBucket, snapshot.MarketCapRank,
            snapshot.Sector, snapshot.EffectiveFrom, snapshot.EffectiveTo, snapshot.IsUnknown, DateTimeOffset.UtcNow);

        return Task.CompletedTask;
    }

    private static UniverseClassificationSnapshot Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        InstrumentId = row.GetInt64("InstrumentId"),
        Symbol = row.GetString("Symbol"),
        MarketCapBucket = row.GetEnum<MarketCapBucket>("MarketCapBucket"),
        MarketCapRank = row.GetNullableInt32("MarketCapRank"),
        Sector = row.GetNullableString("Sector"),
        EffectiveFrom = row.GetDateOnly("EffectiveFrom"),
        EffectiveTo = row.GetNullableDateOnly("EffectiveTo"),
        IsUnknown = row.GetBoolean("IsUnknown"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
