using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteSurveillanceRepository : ISurveillanceRepository
{
    private readonly SqliteConnection _connection;

    public SqliteSurveillanceRepository(SqliteConnection connection) => _connection = connection;

    public Task<SurveillanceStatus> GetLatestStatusAsync(string symbol, DateOnly asOfDate, int maxAgeDays, CancellationToken cancellationToken = default)
    {
        var oldest = asOfDate.AddDays(-Math.Max(0, maxAgeDays));
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM SurveillanceStatuses WHERE Symbol = ? AND AsOfDate <= ? AND AsOfDate >= ? ORDER BY AsOfDate DESC LIMIT 1;",
            Map, symbol, asOfDate, oldest);
        return Task.FromResult(result ?? new SurveillanceStatus { Symbol = symbol, AsOfDate = asOfDate, Flags = RiskFlags.None, Availability = DataAvailability.Unknown, Source = "none" });
    }

    public Task<SurveillanceStatus?> GetMostRecentAsync(string symbol, CancellationToken cancellationToken = default) =>
        Task.FromResult(_connection.QuerySingleOrDefault("SELECT * FROM SurveillanceStatuses WHERE Symbol = ? ORDER BY AsOfDate DESC LIMIT 1;", Map, symbol));

    public Task<SurveillanceStatus> GetStatusAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM SurveillanceStatuses WHERE Symbol = ? AND AsOfDate = ?;",
            Map, symbol, asOfDate);

        // Absence of a record must never be treated as "Safe" (spec §2.3) — return an explicit Unknown, not null.
        return Task.FromResult(result ?? new SurveillanceStatus
        {
            Symbol = symbol,
            AsOfDate = asOfDate,
            Flags = RiskFlags.None,
            Availability = DataAvailability.Unknown,
            Source = null
        });
    }

    public Task UpsertAsync(SurveillanceStatus status, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            INSERT INTO SurveillanceStatuses (Symbol, AsOfDate, Flags, Availability, Source)
            VALUES (?,?,?,?,?)
            ON CONFLICT(Symbol, AsOfDate) DO UPDATE SET
                Flags=excluded.Flags, Availability=excluded.Availability, Source=excluded.Source;
            """,
            status.Symbol, status.AsOfDate, status.Flags, status.Availability, status.Source);

        return Task.CompletedTask;
    }

    private static SurveillanceStatus Map(SqliteRow row) => new()
    {
        Symbol = row.GetString("Symbol"),
        AsOfDate = row.GetDateOnly("AsOfDate"),
        Flags = row.GetEnum<RiskFlags>("Flags"),
        Availability = row.GetEnum<DataAvailability>("Availability"),
        Source = row.GetNullableString("Source"),
    };
}
