using System.Text.Json;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Spec 7.5's scanner export - one row per ranked ScannerResult from a scan cycle.</summary>
public sealed class ScannerReport
{
    public required IReadOnlyList<ScannerResult> Results { get; init; }

    public static ScannerReport Build(IReadOnlyList<ScannerResult> results) => new() { Results = results };

    public string ToJson() => JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] Headers =
    {
        "Rank", "Symbol", "Price", "Sector", "Score", "RelativeStrengthScore", "MomentumScore", "VolumeScore", "VcpScore",
        "Adx", "Rsi", "PivotPrice", "BreakoutStatus", "DistanceFromPivotPercent", "SuggestedEntry", "SuggestedStop", "Risk", "SuggestedPositionSize",
    };

    public string ToCsv() => CsvWriter.Write(Headers, Results.Select(ToRow).ToList());

    public string ToHtml() => HtmlTableRenderer.Render($"Scanner Report ({Results.Count} candidates)", null, Headers, Results.Select(ToRow).ToList());

    private static IReadOnlyList<string> ToRow(ScannerResult r) => new[]
    {
        r.Rank.ToString(), r.Symbol, r.Price.ToString("F2"), r.Sector ?? "n/a", r.Score.ToString("F1"),
        r.RelativeStrengthScore.ToString("F1"), r.MomentumScore.ToString("F1"), r.VolumeScore.ToString("F1"), r.VcpScore.ToString("F1"),
        r.Adx?.ToString("F1") ?? "n/a", r.Rsi?.ToString("F1") ?? "n/a",
        r.PivotPrice.ToString("F2"), r.BreakoutStatus, r.DistanceFromPivotPercent.ToString("F2"),
        r.SuggestedEntry.ToString("F2"), r.SuggestedStop.ToString("F2"), r.Risk.ToString("F2"), r.SuggestedPositionSize.ToString("F0"),
    };
}
