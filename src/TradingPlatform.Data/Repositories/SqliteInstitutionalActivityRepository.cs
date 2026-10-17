using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>SQLite-backed IInstitutionalActivityProvider (spec §2.4). Latest snapshot at/before the date; explicit Unknown when none.</summary>
public sealed class SqliteInstitutionalActivityRepository : IInstitutionalActivityProvider
{
    private readonly SqliteConnection _connection;
    public SqliteInstitutionalActivityRepository(SqliteConnection connection) => _connection = connection;

    public Task<InstitutionalActivitySnapshot> GetActivityAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        var found = _connection.QuerySingleOrDefault(
            "SELECT * FROM InstitutionalActivitySnapshots WHERE Symbol = ? AND AsOfDate <= ? ORDER BY AsOfDate DESC LIMIT 1;", Map, symbol, asOfDate);
        return Task.FromResult(found ?? new InstitutionalActivitySnapshot { Symbol = symbol, AsOfDate = asOfDate, Availability = DataAvailability.Unknown });
    }

    public Task UpsertAsync(InstitutionalActivitySnapshot s, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            INSERT INTO InstitutionalActivitySnapshots (Symbol, AsOfDate, ActivityScore, Availability) VALUES (?,?,?,?)
            ON CONFLICT(Symbol, AsOfDate) DO UPDATE SET ActivityScore=excluded.ActivityScore, Availability=excluded.Availability;
            """,
            s.Symbol, s.AsOfDate, s.ActivityScore, s.Availability);
        return Task.CompletedTask;
    }

    private static InstitutionalActivitySnapshot Map(SqliteRow row) => new()
    {
        Symbol = row.GetString("Symbol"), AsOfDate = row.GetDateOnly("AsOfDate"),
        ActivityScore = row.GetNullableDecimal("ActivityScore"), Availability = row.GetEnum<DataAvailability>("Availability"),
    };
}
