namespace TradingPlatform.Data.Sqlite;

/// <summary>Thrown for any non-OK/ROW/DONE result from the SQLite C API, carrying the native error message.</summary>
public sealed class SqliteException : Exception
{
    public int ResultCode { get; }

    public SqliteException(int resultCode, string message) : base($"SQLite error {resultCode}: {message}")
    {
        ResultCode = resultCode;
    }
}
