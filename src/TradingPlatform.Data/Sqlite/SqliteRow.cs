using System.Globalization;
using System.Text;

namespace TradingPlatform.Data.Sqlite;

/// <summary>
/// A single result row from a prepared statement positioned at SQLITE_ROW.
/// Column values are read out immediately by the reader loop (see
/// SqliteConnection.Query) rather than lazily, so a SqliteRow instance is
/// safe to hold onto after the statement has moved past this row.
///
/// Decimal values are stored/read as invariant round-trip TEXT, never
/// SQLite's native REAL (double) column type — REAL is IEEE-754 double and
/// would silently lose precision on money values, which is not acceptable
/// for a trading ledger. See SqliteValueConverter for the binding side.
/// </summary>
public sealed class SqliteRow
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    internal SqliteRow(object?[] values, IReadOnlyDictionary<string, int> ordinals)
    {
        _values = values;
        _ordinals = ordinals;
    }

    private int Ordinal(string columnName) =>
        _ordinals.TryGetValue(columnName, out var i)
            ? i
            : throw new ArgumentException($"No such column: {columnName}");

    public bool IsDBNull(string column) => _values[Ordinal(column)] is null;

    public string GetString(string column) => (string)_values[Ordinal(column)]!;
    public string? GetNullableString(string column) => (string?)_values[Ordinal(column)];

    public long GetInt64(string column) => Convert.ToInt64(_values[Ordinal(column)]!, CultureInfo.InvariantCulture);
    public long? GetNullableInt64(string column) => _values[Ordinal(column)] is null ? null : GetInt64(column);

    public int GetInt32(string column) => Convert.ToInt32(_values[Ordinal(column)]!, CultureInfo.InvariantCulture);
    public int? GetNullableInt32(string column) => _values[Ordinal(column)] is null ? null : GetInt32(column);

    public bool GetBoolean(string column) => GetInt64(column) != 0;
    public bool? GetNullableBoolean(string column) => _values[Ordinal(column)] is null ? null : GetBoolean(column);

    public double GetDouble(string column) => Convert.ToDouble(_values[Ordinal(column)]!, CultureInfo.InvariantCulture);
    public double? GetNullableDouble(string column) => _values[Ordinal(column)] is null ? null : GetDouble(column);

    /// <summary>Decimal columns are stored as invariant-culture round-trip TEXT — see class remarks.</summary>
    public decimal GetDecimal(string column) => decimal.Parse((string)_values[Ordinal(column)]!, CultureInfo.InvariantCulture);
    public decimal? GetNullableDecimal(string column) => _values[Ordinal(column)] is null ? null : GetDecimal(column);

    public DateTimeOffset GetDateTimeOffset(string column) => DateTimeOffset.Parse((string)_values[Ordinal(column)]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public DateTimeOffset? GetNullableDateTimeOffset(string column) => _values[Ordinal(column)] is null ? null : GetDateTimeOffset(column);

    public DateOnly GetDateOnly(string column) => DateOnly.ParseExact((string)_values[Ordinal(column)]!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public DateOnly? GetNullableDateOnly(string column) => _values[Ordinal(column)] is null ? null : GetDateOnly(column);

    public TEnum GetEnum<TEnum>(string column) where TEnum : struct, Enum => (TEnum)(object)GetInt32(column);
    public TEnum? GetNullableEnum<TEnum>(string column) where TEnum : struct, Enum => _values[Ordinal(column)] is null ? null : GetEnum<TEnum>(column);

    public byte[] GetBlob(string column) => (byte[])_values[Ordinal(column)]!;
}
