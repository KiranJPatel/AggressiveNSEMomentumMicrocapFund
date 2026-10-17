using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>
/// SQLite-backed IFundamentalDataProvider (spec §2.4: manual/CSV/SQLite ingestion — Yahoo fundamentals are not
/// point-in-time reliable, so this never calls the network). Returns the most recent snapshot with AsOfDate &lt;= the
/// requested date (never a FUTURE snapshot — point-in-time safe). With no snapshot at all it returns an explicit
/// Unknown-availability snapshot, never null and never a fabricated "positive".
/// </summary>
public sealed class SqliteFundamentalRepository : IFundamentalDataProvider
{
    private readonly SqliteConnection _connection;
    public SqliteFundamentalRepository(SqliteConnection connection) => _connection = connection;

    public Task<FundamentalSnapshot> GetFundamentalsAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        var found = _connection.QuerySingleOrDefault(
            "SELECT * FROM FundamentalSnapshots WHERE Symbol = ? AND AsOfDate <= ? ORDER BY AsOfDate DESC LIMIT 1;", Map, symbol, asOfDate);
        return Task.FromResult(found ?? new FundamentalSnapshot
        {
            Symbol = symbol, AsOfDate = asOfDate,
            EarningsGrowthAvailability = DataAvailability.Unknown, RevenueGrowthAvailability = DataAvailability.Unknown,
        });
    }

    public Task UpsertAsync(FundamentalSnapshot s, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            INSERT INTO FundamentalSnapshots (Symbol, AsOfDate, EarningsGrowthPercent, EarningsGrowthAvailability, RevenueGrowthPercent, RevenueGrowthAvailability)
            VALUES (?,?,?,?,?,?)
            ON CONFLICT(Symbol, AsOfDate) DO UPDATE SET
                EarningsGrowthPercent=excluded.EarningsGrowthPercent, EarningsGrowthAvailability=excluded.EarningsGrowthAvailability,
                RevenueGrowthPercent=excluded.RevenueGrowthPercent, RevenueGrowthAvailability=excluded.RevenueGrowthAvailability;
            """,
            s.Symbol, s.AsOfDate, s.EarningsGrowthPercent, s.EarningsGrowthAvailability, s.RevenueGrowthPercent, s.RevenueGrowthAvailability);
        return Task.CompletedTask;
    }

    private static FundamentalSnapshot Map(SqliteRow row) => new()
    {
        Symbol = row.GetString("Symbol"), AsOfDate = row.GetDateOnly("AsOfDate"),
        EarningsGrowthPercent = row.GetNullableDecimal("EarningsGrowthPercent"),
        EarningsGrowthAvailability = row.GetEnum<DataAvailability>("EarningsGrowthAvailability"),
        RevenueGrowthPercent = row.GetNullableDecimal("RevenueGrowthPercent"),
        RevenueGrowthAvailability = row.GetEnum<DataAvailability>("RevenueGrowthAvailability"),
    };
}
