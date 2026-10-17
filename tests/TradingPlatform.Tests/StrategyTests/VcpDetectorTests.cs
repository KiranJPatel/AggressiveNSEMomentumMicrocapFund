using TradingPlatform.Core.Configuration;
using TradingPlatform.Strategy.Vcp;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class VcpDetectorTests
{
    private static readonly VcpOptions Options = new();

    [Fact]
    public void GoldenCase_DetectsThreeContractions()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options);

        Assert.True(pattern is not null, "Expected a VCP pattern to be detected on the golden fixture.");
        Assert.Equal(3, pattern!.NumberOfContractions);
        Assert.Equal(3, pattern.ContractionRatios.Count);
    }

    [Fact]
    public void GoldenCase_ContractionRatios_MatchDesignedDepths()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;

        AssertNear(VcpFixture.Depth1, pattern.ContractionRatios[0], 0.01m, "Contraction 1 depth");
        AssertNear(VcpFixture.Depth2, pattern.ContractionRatios[1], 0.01m, "Contraction 2 depth");
        AssertNear(VcpFixture.Depth3, pattern.ContractionRatios[2], 0.01m, "Contraction 3 depth");
    }

    [Fact]
    public void GoldenCase_PivotPrice_IsHighOfFinalContraction()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;
        Assert.Equal(96.5m, pattern.PivotPrice);
    }

    [Fact]
    public void GoldenCase_VcpScore_MatchesIndependentlyComputedValue()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;
        AssertNear(VcpFixture.ExpectedVcpScore, pattern.VcpScore, 0.05m, "VcpScore");
    }

    [Fact]
    public void GoldenCase_VolumeDryUpScore_ReflectsDesignedDryUp()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;
        // Designed as a ~57% volume drop on the final leg vs. the base average.
        AssertNear(56.84m, pattern.VolumeDryUpScore, 0.5m, "VolumeDryUpScore");
    }

    [Fact]
    public void GoldenCase_AverageVolumeDuringBase_MatchesDesignedValue()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;
        AssertNear(118750m, pattern.AverageVolumeDuringBase, 1m, "AverageVolumeDuringBase");
    }

    [Fact]
    public void GoldenCase_BaseLengthDays_SpansFirstHighToLastLow()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), Options)!;
        Assert.Equal(VcpFixture.L2Index - VcpFixture.H0Index, pattern.BaseLengthDays);
    }

    [Fact]
    public void GoldenCase_SymbolAndAsOfDate_MatchLastBar()
    {
        var bars = VcpFixture.BuildGoldenCase();
        var pattern = VcpDetector.Detect(bars, Options)!;

        Assert.Equal(VcpFixture.Symbol, pattern.Symbol);
        Assert.Equal(DateOnly.FromDateTime(bars[^1].Timestamp.DateTime), pattern.AsOfDate);
    }

    [Fact]
    public void WithoutPriorUptrend_ReturnsNull()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildWithoutPriorUptrend(), Options);
        Assert.True(pattern is null, "A base with no genuine prior uptrend must not be recognized as a VCP.");
    }

    [Fact]
    public void TooFewContractions_ReturnsNull()
    {
        var pattern = VcpDetector.Detect(VcpFixture.BuildSingleContractionOnly(), Options);
        Assert.True(pattern is null, "A single contraction is below MinContractions and must not qualify.");
    }

    [Fact]
    public void WideningContractions_ReturnsNull()
    {
        // Each leg is wider than the last (5% -> 10% -> 20%) — the exact
        // opposite of a VCP. Every leg should fail the tightening test
        // against its predecessor and reset, never accumulating 2+ legs.
        var pattern = VcpDetector.Detect(VcpFixture.BuildWideningContractions(), Options);
        Assert.True(pattern is null, "Widening (non-tightening) pullbacks must never be reported as a VCP.");
    }

    [Fact]
    public void EmptyBars_ReturnsNull()
    {
        var pattern = VcpDetector.Detect(Array.Empty<TradingPlatform.Core.Models.HistoricalBar>(), Options);
        Assert.True(pattern is null);
    }

    [Fact]
    public void HigherMinContractionsRequirement_RejectsThreeLegPattern()
    {
        var strictOptions = new VcpOptions { MinContractions = 4 };
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), strictOptions);
        Assert.True(pattern is null, "A 3-leg pattern must not qualify when MinContractions is configured to 4.");
    }

    [Fact]
    public void LooserTighteningRequirement_StillAcceptsGoldenCase()
    {
        // A more permissive ratio (e.g. 0.95 = only 5% tighter required) should still accept a pattern that's genuinely tightening by ~46-57% per leg.
        var looseOptions = new VcpOptions { MaxContractionRatioOfPrevious = 0.95m };
        var pattern = VcpDetector.Detect(VcpFixture.BuildGoldenCase(), looseOptions);
        Assert.True(pattern is not null);
        Assert.Equal(3, pattern!.NumberOfContractions);
    }

    private static void AssertNear(decimal expected, decimal actual, decimal tolerance, string label)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance, $"{label}: expected {expected}, got {actual} (diff {diff} > tolerance {tolerance})");
    }
}
