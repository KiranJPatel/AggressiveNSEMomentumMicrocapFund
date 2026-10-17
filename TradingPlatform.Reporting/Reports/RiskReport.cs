using System.Text.Json;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Spec 7.5's risk export - drawdown circuit-breaker tier transitions plus portfolio-heat summary stats over the reported period.</summary>
public sealed record RiskReport(
    IReadOnlyList<DrawdownEvent> DrawdownEvents,
    decimal? PortfolioHeatAveragePercent, decimal? PortfolioHeatMaxPercent,
    decimal MaxDrawdownPercentObserved, string? CurrentTierName, bool CurrentlyShutDown)
{
    public static RiskReport Build(IReadOnlyList<DrawdownEvent> drawdownEvents, IReadOnlyList<PortfolioSnapshot> snapshots)
    {
        var heatValues = snapshots.Select(s => s.PortfolioHeatPercent).ToList();
        var maxDrawdownObserved = snapshots.Count > 0 ? snapshots.Max(s => s.DrawdownPercent) : 0m;
        var lastEvent = drawdownEvents.OrderBy(e => e.Timestamp).LastOrDefault();

        return new RiskReport(
            DrawdownEvents: drawdownEvents,
            PortfolioHeatAveragePercent: heatValues.Count > 0 ? heatValues.Average() : null,
            PortfolioHeatMaxPercent: heatValues.Count > 0 ? heatValues.Max() : null,
            MaxDrawdownPercentObserved: maxDrawdownObserved,
            CurrentTierName: lastEvent?.TierName,
            CurrentlyShutDown: lastEvent?.TradingShutdown ?? false);
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] EventHeaders = { "Timestamp", "DrawdownPercent", "TierName", "RiskReductionPercent", "TradingShutdown" };

    public string ToCsv() => CsvWriter.Write(EventHeaders, DrawdownEvents.Select(ToRow).ToList());

    public string ToHtml()
    {
        var summary = new List<(string, string)>
        {
            ("Max Drawdown Observed", $"{MaxDrawdownPercentObserved:F2}%"),
            ("Portfolio Heat (avg / max)", $"{F(PortfolioHeatAveragePercent)}% / {F(PortfolioHeatMaxPercent)}%"),
            ("Current Tier", CurrentTierName ?? "n/a"),
            ("Currently Shut Down", CurrentlyShutDown.ToString()),
        };
        return HtmlTableRenderer.Render("Risk Report", summary, EventHeaders, DrawdownEvents.Select(ToRow).ToList());
    }

    private static IReadOnlyList<string> ToRow(DrawdownEvent e) => new[]
    {
        e.Timestamp.ToString("yyyy-MM-dd HH:mm"), e.DrawdownPercent.ToString("F2"), e.TierName,
        e.RiskReductionPercent.ToString("F0"), e.TradingShutdown.ToString(),
    };

    private static string F(decimal? value) => value?.ToString("F2") ?? "n/a";
}
