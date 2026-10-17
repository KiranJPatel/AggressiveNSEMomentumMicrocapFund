using System.Text.Json;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Spec 7.5's daily portfolio export - one row per PortfolioSnapshot.</summary>
public sealed class PortfolioReport
{
    public required IReadOnlyList<PortfolioSnapshot> Snapshots { get; init; }

    public static PortfolioReport Build(IReadOnlyList<PortfolioSnapshot> snapshots) => new() { Snapshots = snapshots };

    public string ToJson() => JsonSerializer.Serialize(Snapshots, new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] Headers =
    {
        "AsOfDate", "Nav", "CashBalance", "PeakNav", "DrawdownPercent", "PortfolioHeatPercent", "OpenPositionCount", "Regime",
    };

    public string ToCsv() => CsvWriter.Write(Headers, Snapshots.Select(ToRow).ToList());

    public string ToHtml() => HtmlTableRenderer.Render($"Portfolio Report ({Snapshots.Count} days)", null, Headers, Snapshots.Select(ToRow).ToList());

    private static IReadOnlyList<string> ToRow(PortfolioSnapshot s) => new[]
    {
        s.AsOfDate.ToString("yyyy-MM-dd"), s.Nav.ToString("F2"), s.CashBalance.ToString("F2"), s.PeakNav.ToString("F2"),
        s.DrawdownPercent.ToString("F2"), s.PortfolioHeatPercent.ToString("F2"), s.OpenPositionCount.ToString(),
        s.Regime?.ToString() ?? "n/a",
    };
}
