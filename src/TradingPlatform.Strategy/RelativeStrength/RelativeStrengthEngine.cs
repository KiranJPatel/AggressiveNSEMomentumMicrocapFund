using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.RelativeStrength;

public sealed record BenchmarkRelativeStrength(string BenchmarkName, RelativeStrengthResult Result);
public sealed record MultiBenchmarkRelativeStrengthResult(IReadOnlyList<BenchmarkRelativeStrength> ByBenchmark, decimal BlendedScore);

/// <summary>
/// Orchestrates relative strength against multiple benchmarks (spec §3.3:
/// "against Nifty 500, sector benchmark, and midcap benchmark where
/// applicable"). Sector and midcap benchmarks are optional — a stock's
/// sector classification or size bucket may be Unknown (spec §2.2/§2.3),
/// in which case only the benchmarks actually supplied contribute, with
/// weights renormalized over them rather than fabricating a neutral score
/// for a missing benchmark.
/// </summary>
public static class RelativeStrengthEngine
{
    public static MultiBenchmarkRelativeStrengthResult Calculate(
        IReadOnlyList<HistoricalBar> stockBars,
        IReadOnlyDictionary<string, IReadOnlyList<HistoricalBar>> benchmarks,
        RsOptions options)
    {
        var byBenchmark = new List<BenchmarkRelativeStrength>();

        foreach (var (name, benchmarkBars) in benchmarks)
        {
            var (stockCloses, benchmarkCloses) = PriceSeriesAligner.AlignByDate(stockBars, benchmarkBars);
            var result = RelativeStrengthCalculator.Calculate(stockCloses, benchmarkCloses, options);
            byBenchmark.Add(new BenchmarkRelativeStrength(name, result));
        }

        var blended = ComputeBlend(byBenchmark, options);
        return new MultiBenchmarkRelativeStrengthResult(byBenchmark, blended);
    }

    private static decimal ComputeBlend(IReadOnlyList<BenchmarkRelativeStrength> byBenchmark, RsOptions options)
    {
        if (byBenchmark.Count == 0) return 50m;

        decimal weightedSum = 0, totalWeight = 0;
        foreach (var entry in byBenchmark)
        {
            var weight = BenchmarkWeight(entry.BenchmarkName, options);
            weightedSum += entry.Result.CompositeScore * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : byBenchmark.Average(b => b.Result.CompositeScore);
    }

    private static decimal BenchmarkWeight(string benchmarkName, RsOptions options) => benchmarkName switch
    {
        BenchmarkNames.BroadIndex => options.BroadIndexWeight,
        BenchmarkNames.Sector => options.SectorBenchmarkWeight,
        BenchmarkNames.Midcap => options.MidcapBenchmarkWeight,
        _ => 0m
    };
}

/// <summary>Well-known benchmark dictionary keys, so callers and the engine agree on naming without magic strings scattered around.</summary>
public static class BenchmarkNames
{
    public const string BroadIndex = "NIFTY500";
    public const string Sector = "SECTOR";
    public const string Midcap = "MIDCAP";
}
