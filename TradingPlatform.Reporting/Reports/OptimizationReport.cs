using System.Text.Json;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>One evaluated parameter combination's outcome, stripped of ParameterGridSearchEngine's generic TOptions payload (Tranche 13) so this report stays independent of which options type was searched.</summary>
public sealed record OptimizationReportRow(IReadOnlyDictionary<string, decimal> ParameterValues, decimal CompositeScore);

/// <summary>Spec 7.5's optimization export - every grid-searched parameter combination ranked by composite score (Tranche 13), plus a highlighted winner.</summary>
public sealed class OptimizationReport
{
    public required IReadOnlyList<OptimizationReportRow> Rows { get; init; }
    public OptimizationReportRow? Winner => Rows.Count > 0 ? Rows[0] : null;

    /// <summary>Rows must already be ranked best-first, matching ParameterGridSearchEngine.Search's own output order - this report never re-sorts, so it can never silently disagree with the engine about which combination actually won.</summary>
    public static OptimizationReport Build(IReadOnlyList<OptimizationReportRow> rankedRows) => new() { Rows = rankedRows };

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToCsv()
    {
        var paramNames = ParameterNames();
        var headers = paramNames.Concat(new[] { "CompositeScore" }).ToList();
        var rows = Rows.Select(r => ToRow(r, paramNames)).ToList();
        return CsvWriter.Write(headers, rows);
    }

    public string ToHtml()
    {
        var paramNames = ParameterNames();
        var headers = paramNames.Concat(new[] { "CompositeScore" }).ToList();
        var rows = Rows.Select(r => ToRow(r, paramNames)).ToList();

        var summary = Winner is null
            ? null
            : Winner.ParameterValues.Select(kv => (kv.Key, kv.Value.ToString("F4")))
                .Append(("CompositeScore", Winner.CompositeScore.ToString("F2")))
                .ToList();

        return HtmlTableRenderer.Render($"Optimization Report ({Rows.Count} combinations)", summary, headers, rows);
    }

    private List<string> ParameterNames() => Rows.Count == 0 ? new List<string>() : Rows[0].ParameterValues.Keys.ToList();

    private static IReadOnlyList<string> ToRow(OptimizationReportRow row, IReadOnlyList<string> paramNames)
    {
        var cells = paramNames.Select(name => row.ParameterValues.TryGetValue(name, out var v) ? v.ToString("F4") : "n/a").ToList();
        cells.Add(row.CompositeScore.ToString("F2"));
        return cells;
    }
}
