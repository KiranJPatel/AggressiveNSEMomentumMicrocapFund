using TradingPlatform.Indicators.Structure;
using TradingPlatform.Strategy.Vcp;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class ZigzagBuilderTests
{
    private static SwingPoint Pt(int idx, SwingPointType type, decimal price) =>
        new(idx, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(idx), price, type, null);

    [Fact]
    public void AlreadyAlternating_PassesThroughUnchanged()
    {
        var raw = new[] { Pt(0, SwingPointType.High, 100m), Pt(3, SwingPointType.Low, 90m), Pt(6, SwingPointType.High, 105m) };
        var result = ZigzagBuilder.Build(raw);

        Assert.Equal(3, result.Count);
        Assert.Equal(100m, result[0].Price);
        Assert.Equal(90m, result[1].Price);
        Assert.Equal(105m, result[2].Price);
    }

    [Fact]
    public void TwoConsecutiveHighs_KeepsTheHigherOne()
    {
        var raw = new[] { Pt(0, SwingPointType.High, 100m), Pt(2, SwingPointType.High, 110m), Pt(5, SwingPointType.Low, 90m) };
        var result = ZigzagBuilder.Build(raw);

        Assert.Equal(2, result.Count);
        Assert.Equal(110m, result[0].Price);
    }

    [Fact]
    public void TwoConsecutiveLows_KeepsTheLowerOne()
    {
        var raw = new[] { Pt(0, SwingPointType.High, 100m), Pt(2, SwingPointType.Low, 95m), Pt(4, SwingPointType.Low, 90m), Pt(7, SwingPointType.High, 105m) };
        var result = ZigzagBuilder.Build(raw);

        Assert.Equal(3, result.Count);
        Assert.Equal(90m, result[1].Price);
    }

    [Fact]
    public void RunOfThreeConsecutiveHighs_KeepsOnlyTheMaximum()
    {
        var raw = new[] { Pt(0, SwingPointType.High, 100m), Pt(2, SwingPointType.High, 95m), Pt(4, SwingPointType.High, 108m), Pt(7, SwingPointType.Low, 90m) };
        var result = ZigzagBuilder.Build(raw);

        Assert.Equal(2, result.Count);
        Assert.Equal(108m, result[0].Price);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = ZigzagBuilder.Build(Array.Empty<SwingPoint>());
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void UnorderedInput_IsSortedByBarIndexFirst()
    {
        var raw = new[] { Pt(5, SwingPointType.Low, 90m), Pt(0, SwingPointType.High, 100m) };
        var result = ZigzagBuilder.Build(raw);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].BarIndex);
        Assert.Equal(5, result[1].BarIndex);
    }
}
