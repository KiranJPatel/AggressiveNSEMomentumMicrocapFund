namespace TradingPlatform.Data.Sqlite;

/// <summary>
/// BEGIN IMMEDIATE / COMMIT / ROLLBACK wrapper. Rolls back automatically on
/// Dispose if neither Commit nor Rollback was called, so an exception
/// partway through a batch write never leaves a half-applied transaction
/// open (spec §7.5: batch DB writes for performance, but never at the cost
/// of leaving inconsistent state).
/// </summary>
public sealed class SqliteTransaction : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _completed;

    internal SqliteTransaction(SqliteConnection connection) => _connection = connection;

    public void Commit()
    {
        if (_completed) return;
        _connection.Execute("COMMIT;");
        _completed = true;
    }

    public void Rollback()
    {
        if (_completed) return;
        _connection.Execute("ROLLBACK;");
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed) Rollback();
    }
}
