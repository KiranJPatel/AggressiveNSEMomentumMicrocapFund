using System.Text;

namespace TradingPlatform.Reporting.Export;

/// <summary>
/// Hand-rolled CSV writer (spec §7.5's CSV export requirement, no NuGet
/// CSV library available offline — spec §1.3). RFC 4180-style quoting:
/// any field containing a comma, quote, or newline is wrapped in quotes
/// with internal quotes doubled.
/// </summary>
public static class CsvWriter
{
    public static string Write(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Escape)));
        sb.Append('\n');
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Escape)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
