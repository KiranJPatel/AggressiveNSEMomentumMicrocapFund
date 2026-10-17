using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteOptimizationRunRepository : IOptimizationRunRepository
{
    private readonly SqliteConnection _connection;

    public SqliteOptimizationRunRepository(SqliteConnection connection) => _connection = connection;

    public Task<string> InsertBatchAsync(IReadOnlyList<OptimizationRunRecord> candidates, CancellationToken cancellationToken = default)
    {
        var batchId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var now = DateTimeOffset.UtcNow;
        using var tx = _connection.BeginTransaction();
        foreach (var c in candidates)
        {
            _connection.Execute(
                "INSERT INTO OptimizationRuns (BatchId, ParameterSetJson, CompositeScoreBreakdownJson, CompositeScore, IsOutOfSample, CreatedAt) VALUES (?,?,?,?,?,?);",
                batchId, c.ParameterSetJson, c.CompositeScoreBreakdownJson, c.CompositeScore, c.IsOutOfSample, now);
        }
        tx.Commit();
        return Task.FromResult(batchId);
    }

    public Task<IReadOnlyList<OptimizationRunRecord>> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.Query("SELECT * FROM OptimizationRuns WHERE BatchId = ? ORDER BY CAST(CompositeScore AS REAL) DESC, Id;", Map, batchId));

    public Task<IReadOnlyList<OptimizationRunRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(_connection.Query("SELECT * FROM OptimizationRuns ORDER BY Id DESC LIMIT ?;", Map, limit));

    private static OptimizationRunRecord Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        BatchId = row.GetNullableString("BatchId") ?? "",
        ParameterSetJson = row.GetString("ParameterSetJson"),
        CompositeScoreBreakdownJson = row.GetString("CompositeScoreBreakdownJson"),
        CompositeScore = row.GetDecimal("CompositeScore"),
        IsOutOfSample = row.GetBoolean("IsOutOfSample"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
