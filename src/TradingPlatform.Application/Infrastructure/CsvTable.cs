using System.Text;

namespace TradingPlatform.Application.Infrastructure;

/// <summary>Minimal RFC-4180 CSV reader (quoted fields, escaped quotes, embedded commas/newlines, BOM, CRLF). Header-driven, case-insensitive columns.</summary>
public sealed class CsvTable
{
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<CsvRow> Rows { get; }
    private CsvTable(IReadOnlyList<string> headers, IReadOnlyList<CsvRow> rows) { Headers = headers; Rows = rows; }

    public bool HasColumn(string name) => Headers.Any(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static CsvTable Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var records = new List<(int Line, List<string> Fields)>();
        var field = new StringBuilder(); var fields = new List<string>();
        var inQuotes = false; var line = 1; var recordStartLine = 1; var anyContent = false;

        void EndRecord()
        {
            fields.Add(field.ToString()); field.Clear();
            if (anyContent || fields.Count > 1) records.Add((recordStartLine, fields));
            fields = new List<string>(); anyContent = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else inQuotes = false; }
                else { if (c == '\n') line++; field.Append(c); }
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; anyContent = true; break;
                case ',': fields.Add(field.ToString()); field.Clear(); anyContent = true; break;
                case '\r': break;
                case '\n': EndRecord(); line++; recordStartLine = line; break;
                default: field.Append(c); if (!char.IsWhiteSpace(c)) anyContent = true; break;
            }
        }
        if (inQuotes) throw new FormatException("CSV ends inside an unterminated quoted field.");
        if (field.Length > 0 || fields.Count > 0) EndRecord();
        if (records.Count == 0) throw new FormatException("CSV is empty.");

        var headers = records[0].Fields.Select(h => h.Trim()).ToList();
        var rows = records.Skip(1).Select(r => new CsvRow(r.Line, headers, r.Fields)).ToList();
        return new CsvTable(headers, rows);
    }
}

public sealed class CsvRow
{
    public int Line { get; }
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public int FieldCount { get; }
    public int HeaderCount { get; }

    public CsvRow(int line, IReadOnlyList<string> headers, IReadOnlyList<string> fields)
    {
        Line = line; FieldCount = fields.Count; HeaderCount = headers.Count;
        for (var i = 0; i < headers.Count && i < fields.Count; i++) _values[headers[i]] = fields[i].Trim();
    }

    /// <summary>Trimmed value, or null when the column is absent or blank.</summary>
    public string? Get(string column) => _values.TryGetValue(column, out var v) && v.Length > 0 ? v : null;
}
