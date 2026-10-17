using System.Text;

namespace TradingPlatform.Reporting.Export;

/// <summary>
/// Minimal, dependency-free HTML export (spec §7.5). Renders a titled
/// page with an optional key-value summary block (for report headers like
/// spec §8.1's ASCII-art summary) followed by an optional data table —
/// either or both may be omitted depending on the report shape (a
/// BacktestReport is mostly summary; a TradeReport is mostly table).
/// </summary>
public static class HtmlTableRenderer
{
    public static string Render(string title, IReadOnlyList<(string Label, string Value)>? summary, IReadOnlyList<string>? tableHeaders, IReadOnlyList<IReadOnlyList<string>>? tableRows)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>").Append(Encode(title)).Append("</title>\n");
        sb.Append("<style>body{font-family:sans-serif;margin:2em;color:#222}h1{font-size:1.3em}table{border-collapse:collapse;margin-top:1em}");
        sb.Append("th,td{border:1px solid #ccc;padding:4px 10px;text-align:right}th{background:#f0f0f0;text-align:center}");
        sb.Append("dl{display:grid;grid-template-columns:max-content 1fr;gap:2px 12px}dt{font-weight:600}</style></head><body>\n");
        sb.Append("<h1>").Append(Encode(title)).Append("</h1>\n");

        if (summary is { Count: > 0 })
        {
            sb.Append("<dl>\n");
            foreach (var (label, value) in summary)
                sb.Append("<dt>").Append(Encode(label)).Append("</dt><dd>").Append(Encode(value)).Append("</dd>\n");
            sb.Append("</dl>\n");
        }

        if (tableHeaders is { Count: > 0 } && tableRows is not null)
        {
            sb.Append("<table>\n<tr>");
            foreach (var h in tableHeaders) sb.Append("<th>").Append(Encode(h)).Append("</th>");
            sb.Append("</tr>\n");
            foreach (var row in tableRows)
            {
                sb.Append("<tr>");
                foreach (var cell in row) sb.Append("<td>").Append(Encode(cell)).Append("</td>");
                sb.Append("</tr>\n");
            }
            sb.Append("</table>\n");
        }

        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    private static string Encode(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
