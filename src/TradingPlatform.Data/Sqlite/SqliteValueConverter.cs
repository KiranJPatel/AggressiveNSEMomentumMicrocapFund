using System.Globalization;
using System.Text;
using static TradingPlatform.Data.Sqlite.NativeMethods;

namespace TradingPlatform.Data.Sqlite;

/// <summary>
/// Binds CLR values (as passed to SqliteConnection.Execute/Query) onto a
/// prepared statement, and reads a fully-materialized row's worth of
/// values back out as boxed CLR values for SqliteRow. See SqliteRow's
/// class remarks for why decimal is TEXT, not REAL.
/// </summary>
internal static class SqliteValueConverter
{
    public static void Bind(IntPtr stmt, int oneBasedIndex, object? value)
    {
        int rc;
        switch (value)
        {
            case null:
                rc = sqlite3_bind_null(stmt, oneBasedIndex);
                break;
            case bool b:
                rc = sqlite3_bind_int64(stmt, oneBasedIndex, b ? 1 : 0);
                break;
            case int i:
                rc = sqlite3_bind_int64(stmt, oneBasedIndex, i);
                break;
            case long l:
                rc = sqlite3_bind_int64(stmt, oneBasedIndex, l);
                break;
            case Enum e:
                rc = sqlite3_bind_int64(stmt, oneBasedIndex, Convert.ToInt64(e, CultureInfo.InvariantCulture));
                break;
            case double d:
                rc = sqlite3_bind_double(stmt, oneBasedIndex, d);
                break;
            case float f:
                rc = sqlite3_bind_double(stmt, oneBasedIndex, f);
                break;
            case decimal dec:
                // Invariant round-trip TEXT — see SqliteRow class remarks (never REAL/double for money).
                rc = BindUtf8Text(stmt, oneBasedIndex, dec.ToString("G", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                rc = BindUtf8Text(stmt, oneBasedIndex, dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                rc = BindUtf8Text(stmt, oneBasedIndex, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case string s:
                rc = BindUtf8Text(stmt, oneBasedIndex, s);
                break;
            case byte[] blob:
                rc = sqlite3_bind_blob(stmt, oneBasedIndex, blob, blob.Length, SQLITE_TRANSIENT);
                break;
            default:
                throw new NotSupportedException($"Cannot bind CLR type {value.GetType().FullName} as a SQLite parameter.");
        }

        if (rc != SQLITE_OK)
            throw new SqliteException(rc, $"Failed to bind parameter #{oneBasedIndex} ({value?.GetType().Name ?? "null"}).");
    }

    private static int BindUtf8Text(IntPtr stmt, int oneBasedIndex, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return sqlite3_bind_text(stmt, oneBasedIndex, bytes, bytes.Length, SQLITE_TRANSIENT);
    }

    /// <summary>Reads the value at the given zero-based column index of the current row as a boxed CLR value.</summary>
    public static object? ReadColumn(IntPtr stmt, int zeroBasedCol)
    {
        var type = sqlite3_column_type(stmt, zeroBasedCol);
        return type switch
        {
            SQLITE_NULL => null,
            SQLITE_INTEGER => sqlite3_column_int64(stmt, zeroBasedCol),
            SQLITE_FLOAT => sqlite3_column_double(stmt, zeroBasedCol),
            SQLITE_TEXT => ReadUtf8Text(stmt, zeroBasedCol),
            SQLITE_BLOB => ReadBlob(stmt, zeroBasedCol),
            _ => throw new NotSupportedException($"Unknown SQLite column type code {type}.")
        };
    }

    private static string ReadUtf8Text(IntPtr stmt, int zeroBasedCol)
    {
        var ptr = sqlite3_column_text(stmt, zeroBasedCol);
        var len = sqlite3_column_bytes(stmt, zeroBasedCol);
        if (ptr == IntPtr.Zero || len == 0) return string.Empty;
        var buf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    private static byte[] ReadBlob(IntPtr stmt, int zeroBasedCol)
    {
        var ptr = sqlite3_column_blob(stmt, zeroBasedCol);
        var len = sqlite3_column_bytes(stmt, zeroBasedCol);
        if (ptr == IntPtr.Zero || len == 0) return Array.Empty<byte>();
        var buf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, len);
        return buf;
    }

    public static string GetColumnName(IntPtr stmt, int zeroBasedCol)
    {
        var ptr = sqlite3_column_name(stmt, zeroBasedCol);
        return ptr == IntPtr.Zero ? $"col{zeroBasedCol}" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr) ?? $"col{zeroBasedCol}";
    }
}
