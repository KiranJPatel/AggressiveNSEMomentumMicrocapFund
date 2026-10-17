using System.Text.Json;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Spec 7.5's per-trade export - one row per closed Trade.</summary>
public sealed class TradeReport
{
    public required IReadOnlyList<Trade> Trades { get; init; }

    public static TradeReport Build(IReadOnlyList<Trade> trades) => new() { Trades = trades };

    public string ToJson() => JsonSerializer.Serialize(Trades, new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] Headers =
    {
        "Symbol", "EntryAt", "EntryPrice", "ExitAt", "ExitPrice", "ExitReason",
        "InitialStop", "InitialRiskAmount", "RealizedPnl", "RMultiple",
        "MaximumFavorableExcursion", "MaximumAdverseExcursion", "RegimeScoreAtEntry", "StockScoreAtEntry",
    };

    public string ToCsv() => CsvWriter.Write(Headers, Trades.Select(ToRow).ToList());

    public string ToHtml() => HtmlTableRenderer.Render($"Trade Report ({Trades.Count} trades)", null, Headers, Trades.Select(ToRow).ToList());

    private static IReadOnlyList<string> ToRow(Trade t) => new[]
    {
        t.Symbol, t.EntryAt.ToString("yyyy-MM-dd"), t.EntryPrice.ToString("F2"),
        t.ExitAt.ToString("yyyy-MM-dd"), t.ExitPrice.ToString("F2"), t.ExitReason.ToString(),
        t.InitialStop.ToString("F2"), t.InitialRiskAmount.ToString("F2"), t.RealizedPnl.ToString("F2"), t.RMultiple.ToString("F2"),
        t.MaximumFavorableExcursion.ToString("F2"), t.MaximumAdverseExcursion.ToString("F2"),
        t.RegimeScoreAtEntry.ToString(), t.StockScoreAtEntry.ToString("F1"),
    };
}
