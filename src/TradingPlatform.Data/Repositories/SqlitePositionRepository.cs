using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqlitePositionRepository : IPositionRepository
{
    private readonly SqliteConnection _connection;

    public SqlitePositionRepository(SqliteConnection connection) => _connection = connection;

    public Task<long> UpsertAsync(Position position, CancellationToken cancellationToken = default)
    {
        if (position.Id == 0)
        {
            var newId = _connection.ExecuteInsert(
                """
                INSERT INTO Positions
                    (Symbol, Quantity, AverageEntryPrice, InitialStop, CurrentStop, InitialRiskAmount,
                     RiskPerShare, HighestStageFilled, IsOpen, OpenedAt, ClosedAt, CloseReason)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?);
                """,
                position.Symbol, position.Quantity, position.AverageEntryPrice, position.InitialStop,
                position.CurrentStop, position.InitialRiskAmount, position.RiskPerShare,
                position.HighestStageFilled, position.IsOpen, position.OpenedAt, position.ClosedAt,
                position.CloseReason);
            return Task.FromResult(newId);
        }

        _connection.Execute(
            """
            UPDATE Positions SET Symbol=?, Quantity=?, AverageEntryPrice=?, InitialStop=?, CurrentStop=?,
                InitialRiskAmount=?, RiskPerShare=?, HighestStageFilled=?, IsOpen=?, OpenedAt=?, ClosedAt=?, CloseReason=?
            WHERE Id=?;
            """,
            position.Symbol, position.Quantity, position.AverageEntryPrice, position.InitialStop,
            position.CurrentStop, position.InitialRiskAmount, position.RiskPerShare,
            position.HighestStageFilled, position.IsOpen, position.OpenedAt, position.ClosedAt,
            position.CloseReason, position.Id);
        return Task.FromResult(position.Id);
    }

    public Task<Position?> GetOpenBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM Positions WHERE Symbol = ? AND IsOpen = 1;", Map, symbol);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Position>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM Positions WHERE IsOpen = 1 ORDER BY Symbol;", Map);
        return Task.FromResult(result);
    }

    public Task CloseAsync(long positionId, DateTimeOffset closedAt, ExitReason reason, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            "UPDATE Positions SET IsOpen = 0, ClosedAt = ?, CloseReason = ? WHERE Id = ?;",
            closedAt, reason, positionId);
        return Task.CompletedTask;
    }

    private static Position Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        Quantity = row.GetDecimal("Quantity"),
        AverageEntryPrice = row.GetDecimal("AverageEntryPrice"),
        InitialStop = row.GetDecimal("InitialStop"),
        CurrentStop = row.GetDecimal("CurrentStop"),
        InitialRiskAmount = row.GetDecimal("InitialRiskAmount"),
        RiskPerShare = row.GetDecimal("RiskPerShare"),
        HighestStageFilled = row.GetEnum<PyramidStage>("HighestStageFilled"),
        IsOpen = row.GetBoolean("IsOpen"),
        OpenedAt = row.GetDateTimeOffset("OpenedAt"),
        ClosedAt = row.GetNullableDateTimeOffset("ClosedAt"),
        CloseReason = row.GetNullableEnum<ExitReason>("CloseReason"),
    };
}
