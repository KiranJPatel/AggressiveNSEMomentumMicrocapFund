namespace TradingPlatform.Reporting;

public sealed record CapitalGrowthScenario(decimal AnnualReturnPercent, int Years, decimal ProjectedCapital);

/// <summary>
/// Spec §8.3: "Hypothetical growth from ₹10 lakh at 20/30/40/50/60/80/100%
/// annual return over 3/5/10 years — clearly labeled as mathematical
/// scenarios, not forecasts." Pure compounding arithmetic — this
/// deliberately has NO connection to any backtest result; it's a
/// what-if table, and every consumer (report text, HTML) must keep spec's
/// explicit "not a forecast" framing directly alongside it, not implied.
/// </summary>
public static class CapitalGrowthSimulator
{
    public static readonly IReadOnlyList<decimal> DefaultAnnualReturnPercents = new decimal[] { 20m, 30m, 40m, 50m, 60m, 80m, 100m };
    public static readonly IReadOnlyList<int> DefaultYears = new[] { 3, 5, 10 };

    public static IReadOnlyList<CapitalGrowthScenario> Simulate(decimal initialCapital, IReadOnlyList<decimal>? annualReturnPercents = null, IReadOnlyList<int>? years = null)
    {
        var returns = annualReturnPercents ?? DefaultAnnualReturnPercents;
        var horizons = years ?? DefaultYears;

        var scenarios = new List<CapitalGrowthScenario>(returns.Count * horizons.Count);
        foreach (var annualReturnPercent in returns)
        foreach (var y in horizons)
        {
            var growthFactor = Math.Pow(1.0 + (double)(annualReturnPercent / 100m), y);
            scenarios.Add(new CapitalGrowthScenario(annualReturnPercent, y, initialCapital * (decimal)growthFactor));
        }
        return scenarios;
    }
}
