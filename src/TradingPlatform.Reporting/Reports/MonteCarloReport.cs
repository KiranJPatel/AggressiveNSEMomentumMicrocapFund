using System.Text.Json;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Reporting.Export;

namespace TradingPlatform.Reporting.Reports;

/// <summary>Spec 7.5/8.3's Monte Carlo export - risk-of-ruin/percentile results (Tranche 13) plus the separate hypothetical capital-growth scenario table (spec 8.3). The two are unrelated calculations bundled into one report only because spec presents them together in 8.3 - GrowthScenarios must never be read as validated by the Monte Carlo run above it; CapitalGrowthSimulator's own doc makes that framing explicit.</summary>
public sealed record MonteCarloReport(MonteCarloResult MonteCarlo, IReadOnlyList<CapitalGrowthScenario> GrowthScenarios)
{
    public static MonteCarloReport Build(MonteCarloResult monteCarlo, decimal initialCapital) =>
        new(monteCarlo, CapitalGrowthSimulator.Simulate(initialCapital));

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] GrowthHeaders = { "AnnualReturnPercent", "Years", "ProjectedCapital" };

    public string ToCsv() => CsvWriter.Write(GrowthHeaders, GrowthScenarios.Select(ToRow).ToList());

    public string ToHtml()
    {
        var summary = new List<(string, string)>
        {
            ("Iterations", MonteCarlo.IterationCount.ToString()),
            ("P5 Ending Capital", MonteCarlo.P5EndingCapital.ToString("F0")),
            ("P25 Ending Capital", MonteCarlo.P25EndingCapital.ToString("F0")),
            ("Median Ending Capital", MonteCarlo.MedianEndingCapital.ToString("F0")),
            ("P75 Ending Capital", MonteCarlo.P75EndingCapital.ToString("F0")),
            ("P95 Ending Capital", MonteCarlo.P95EndingCapital.ToString("F0")),
            ("P(Drawdown > 20%)", $"{MonteCarlo.ProbabilityDrawdownExceeds20Percent:F1}%"),
            ("P(Drawdown > 30%)", $"{MonteCarlo.ProbabilityDrawdownExceeds30Percent:F1}%"),
            ("P(Drawdown > 40%)", $"{MonteCarlo.ProbabilityDrawdownExceeds40Percent:F1}%"),
            ("Probability of Ruin", $"{MonteCarlo.ProbabilityOfRuin:F1}%"),
            ("NOTE", "Capital growth scenarios below are hypothetical mathematical projections, not forecasts (spec 8.3)."),
        };
        return HtmlTableRenderer.Render("Monte Carlo Report", summary, GrowthHeaders, GrowthScenarios.Select(ToRow).ToList());
    }

    private static IReadOnlyList<string> ToRow(CapitalGrowthScenario s) => new[]
    {
        s.AnnualReturnPercent.ToString("F0"), s.Years.ToString(), s.ProjectedCapital.ToString("F0"),
    };
}
