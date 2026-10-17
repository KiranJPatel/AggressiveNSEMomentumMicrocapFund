using static TradingPlatform.Data.Sqlite.NativeMethods;

namespace TradingPlatform.Data.Sqlite;

/// <summary>
/// Minimal hand-rolled ADO-like wrapper over the SQLite C API (see
/// NativeMethods and docs/gap-log.md — Microsoft.Data.Sqlite is
/// unavailable, no NuGet access). Single logical connection; all
/// execute/query paths are serialized with an internal lock since a
/// single sqlite3* handle is not safe for concurrent use from multiple
/// threads without external synchronization, and this app has several
/// background workers (scanner, reconciliation, etc.) sharing one DB file.
/// </summary>
public sealed class SqliteConnection : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _db;
    private bool _disposed;

    public string DatabasePath { get; }

    public SqliteConnection(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var rc = sqlite3_open_v2(databasePath, out _db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            var message = _db == IntPtr.Zero ? "(no db handle)" : GetErrorMessage(_db);
            throw new SqliteException(rc, $"Failed to open database at '{databasePath}': {message}");
        }

        sqlite3_busy_timeout(_db, 5000);

        // WAL: the dashboard API (`serve`) and a CLI run (`paper`, `scan`, `download`) routinely touch the same file at once; in the default
        // rollback-journal mode a writer blocks every reader and a crash mid-write leaves a hot journal to recover. WAL lets readers proceed
        // during a write and recovers by replaying the log. (synchronous stays FULL: every commit is durable — this is a trading journal.)
        // foreign_keys is OFF by default per connection in SQLite; the schema declares its parent/child links, so enforce them.
        Pragma("PRAGMA journal_mode=WAL;");
        Pragma("PRAGMA foreign_keys=ON;");
    }

    private void Pragma(string sql)
    {
        // Best-effort: on a filesystem without shared-memory support WAL silently stays in the previous mode; that must not stop the app.
        try { Execute(sql); }
        catch (SqliteException) { /* leave the default; correctness does not depend on it */ }
    }

    /// <summary>Executes DDL/DML with no result set. Returns sqlite3_changes() (rows affected) — meaningless for DDL.</summary>
    public int Execute(string sql, params object?[] parameters)
    {
        lock (_gate)
        {
            using var stmt = Prepare(sql, parameters);
            var rc = sqlite3_step(stmt.Handle);
            if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                throw new SqliteException(rc, $"Execute failed for '{sql}': {GetErrorMessage(_db)}");
            return sqlite3_changes(_db);
        }
    }

    /// <summary>Executes an INSERT and returns sqlite3_last_insert_rowid().</summary>
    public long ExecuteInsert(string sql, params object?[] parameters)
    {
        lock (_gate)
        {
            using var stmt = Prepare(sql, parameters);
            var rc = sqlite3_step(stmt.Handle);
            if (rc != SQLITE_DONE)
                throw new SqliteException(rc, $"Insert failed for '{sql}': {GetErrorMessage(_db)}");
            return sqlite3_last_insert_rowid(_db);
        }
    }

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteRow, T> map, params object?[] parameters)
    {
        lock (_gate)
        {
            using var stmt = Prepare(sql, parameters);
            var results = new List<T>();
            var columnCount = sqlite3_column_count(stmt.Handle);
            var ordinals = BuildOrdinals(stmt.Handle, columnCount);

            int rc;
            while ((rc = sqlite3_step(stmt.Handle)) == SQLITE_ROW)
            {
                var values = new object?[columnCount];
                for (var c = 0; c < columnCount; c++)
                    values[c] = SqliteValueConverter.ReadColumn(stmt.Handle, c);

                results.Add(map(new SqliteRow(values, ordinals)));
            }

            if (rc != SQLITE_DONE)
                throw new SqliteException(rc, $"Query failed for '{sql}': {GetErrorMessage(_db)}");

            return results;
        }
    }

    /// <summary>
    /// Returns the single matching row as a reference-type T, or null if none.
    /// For value-type results (long, decimal, etc.) use TryQuerySingleValue
    /// instead — see its remarks for why: unconstrained "T?" does NOT compile
    /// to Nullable&lt;T&gt; when T ends up being a value type (that only
    /// happens with an explicit "where T : struct" constraint); without it,
    /// "T?" silently erases to plain T, so "no rows" was indistinguishable
    /// from a genuine row containing 0 (caught by
    /// RepositoryTests.InstrumentRepository_Upsert_ThenGetBySymbol_RoundTrips —
    /// see docs/gap-log.md for the full story).
    /// </summary>
    public T? QuerySingleOrDefault<T>(string sql, Func<SqliteRow, T> map, params object?[] parameters) where T : class
    {
        var results = Query(sql, map, parameters);
        if (results.Count == 0) return null;
        if (results.Count > 1) throw new InvalidOperationException($"Expected 0 or 1 row but got {results.Count}. SQL: {sql}");
        return results[0];
    }

    /// <summary>Value-type counterpart to QuerySingleOrDefault — see its remarks for why this can't just be one generic method.</summary>
    public bool TryQuerySingleValue<T>(string sql, Func<SqliteRow, T> map, out T value, params object?[] parameters) where T : struct
    {
        var results = Query(sql, map, parameters);
        if (results.Count == 0) { value = default; return false; }
        if (results.Count > 1) throw new InvalidOperationException($"Expected 0 or 1 row but got {results.Count}. SQL: {sql}");
        value = results[0];
        return true;
    }

    public SqliteTransaction BeginTransaction()
    {
        Execute("BEGIN IMMEDIATE;");
        return new SqliteTransaction(this);
    }

    private static IReadOnlyDictionary<string, int> BuildOrdinals(IntPtr stmt, int columnCount)
    {
        var map = new Dictionary<string, int>(columnCount, StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < columnCount; c++)
            map[SqliteValueConverter.GetColumnName(stmt, c)] = c;
        return map;
    }

    private PreparedStatement Prepare(string sql, object?[] parameters)
    {
        var rc = sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero);
        if (rc != SQLITE_OK)
            throw new SqliteException(rc, $"Failed to prepare '{sql}': {GetErrorMessage(_db)}");

        for (var i = 0; i < parameters.Length; i++)
            SqliteValueConverter.Bind(stmt, i + 1, parameters[i]);

        return new PreparedStatement(stmt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_db != IntPtr.Zero)
        {
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }

    private readonly struct PreparedStatement : IDisposable
    {
        public IntPtr Handle { get; }
        public PreparedStatement(IntPtr handle) => Handle = handle;
        public void Dispose() => sqlite3_finalize(Handle);
    }
}
