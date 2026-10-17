using System.Runtime.InteropServices;

namespace TradingPlatform.Data.Sqlite;

/// <summary>
/// Raw P/Invoke bindings against the system's libsqlite3 (spec §5.2/§7.2
/// call for SQLite persistence; Microsoft.Data.Sqlite is a NuGet package
/// and this environment has no NuGet access — see docs/gap-log.md).
/// libsqlite3.so.0 is present locally as an OS-level dependency (confirmed
/// via `ldconfig -p`), so this binds directly against the C API instead.
/// Only the subset of the API this project actually uses is declared.
/// </summary>
internal static class NativeMethods
{
    private const string Lib = "libsqlite3.so.0";

    // Open flags (sqlite3.h)
    public const int SQLITE_OPEN_READWRITE = 0x00000002;
    public const int SQLITE_OPEN_CREATE = 0x00000004;

    // Result codes actually branched on by SqliteConnection/SqliteRow
    public const int SQLITE_OK = 0;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;

    // Column/fundamental types (sqlite3_column_type)
    public const int SQLITE_INTEGER = 1;
    public const int SQLITE_FLOAT = 2;
    public const int SQLITE_TEXT = 3;
    public const int SQLITE_BLOB = 4;
    public const int SQLITE_NULL = 5;

    /// <summary>Passed as the destructor pointer for sqlite3_bind_text/blob meaning "SQLITE_TRANSIENT": SQLite copies the buffer immediately.</summary>
    public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_open_v2(string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_extended_errcode(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_exec(IntPtr db, string sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sqlite3_free(IntPtr ptr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_prepare_v2(IntPtr db, string sql, int nBytes, out IntPtr stmt, IntPtr tail);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_reset(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_clear_bindings(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_null(IntPtr stmt, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int nBytes, IntPtr destructor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int nBytes, IntPtr destructor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_type(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern double sqlite3_column_double(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_last_insert_rowid(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_changes(IntPtr db);

    public static string GetErrorMessage(IntPtr db)
    {
        var ptr = sqlite3_errmsg(db);
        return ptr == IntPtr.Zero ? "(no error message)" : Marshal.PtrToStringUTF8(ptr) ?? "(unreadable error message)";
    }
}
