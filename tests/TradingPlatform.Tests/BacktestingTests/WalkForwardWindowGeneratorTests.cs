using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class WalkForwardWindowGeneratorTests
{
    [Fact]
    public void SingleWindow_FitsExactly_ProducesOneWindow()
    {
        var start = new DateOnly(2015, 1, 1);
        // Contiguous sub-periods (each starts the day after the previous ends) push the exact
        // testEnd a few days past a naive "start + 4 years" — compute it the same way the
        // generator does rather than assume a round boundary.
        var end = new DateOnly(2019, 1, 1);

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6);

        Assert.Equal(1, windows.Count);
        Assert.Equal(start, windows[0].TrainStart);
    }

    [Fact]
    public void Windows_AreContiguous_WithinOneWindow()
    {
        var start = new DateOnly(2010, 1, 1);
        var end = new DateOnly(2025, 12, 31);

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6);
        var w = windows[0];

        Assert.Equal(w.TrainEnd.AddDays(1), w.ValidateStart);
        Assert.Equal(w.ValidateEnd.AddDays(1), w.TestStart);
    }

    [Fact]
    public void MultipleWindows_StepForwardByTestMonths_ByDefault()
    {
        var start = new DateOnly(2010, 1, 1);
        var end = new DateOnly(2025, 12, 31);

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6);

        Assert.True(windows.Count > 1);
        for (var i = 1; i < windows.Count; i++)
            Assert.Equal(windows[i - 1].TrainStart.AddMonths(6), windows[i].TrainStart);
    }

    [Fact]
    public void NoWindowFits_ReturnsEmpty_NeverThrows()
    {
        var start = new DateOnly(2020, 1, 1);
        var end = new DateOnly(2020, 6, 1); // far too short for a 3y/6mo/6mo window

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6);

        Assert.Equal(0, windows.Count);
    }

    [Fact]
    public void CustomStepMonths_OverridesDefault()
    {
        var start = new DateOnly(2010, 1, 1);
        var end = new DateOnly(2020, 12, 31);

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6, stepMonths: 3);

        Assert.True(windows.Count >= 2);
        Assert.Equal(windows[0].TrainStart.AddMonths(3), windows[1].TrainStart);
    }

    [Fact]
    public void StartAfterEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() => WalkForwardWindowGenerator.GenerateWindows(new DateOnly(2020, 1, 1), new DateOnly(2019, 1, 1), 3, 6, 6));
    }

    [Fact]
    public void EveryWindow_NeverExtendsPastOverallEnd()
    {
        var start = new DateOnly(2010, 1, 1);
        var end = new DateOnly(2022, 3, 17);

        var windows = WalkForwardWindowGenerator.GenerateWindows(start, end, trainYears: 3, validateMonths: 6, testMonths: 6);

        Assert.True(windows.All(w => w.TestEnd <= end));
    }
}
