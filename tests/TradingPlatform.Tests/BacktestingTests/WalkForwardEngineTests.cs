using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class WalkForwardEngineTests
{
    [Fact]
    public void Run_CallsDelegateThreeTimesPerWindow_WithCorrectDateRanges()
    {
        var windows = WalkForwardWindowGenerator.GenerateWindows(new DateOnly(2010, 1, 1), new DateOnly(2020, 12, 31), 3, 6, 6);
        var calls = new List<(DateOnly Start, DateOnly End)>();

        var results = WalkForwardEngine.Run(windows, (start, end) =>
        {
            calls.Add((start, end));
            return SelectionFixture.MakeResult(start, end, new[] { SelectionFixture.MakeTrade("ABC", 100m) });
        });

        Assert.Equal(windows.Count * 3, calls.Count);
        Assert.Equal(results.Count, windows.Count);
    }

    [Fact]
    public void Run_EachWindowResult_MapsTrainValidateTest_ToCorrectSubPeriods()
    {
        var windows = WalkForwardWindowGenerator.GenerateWindows(new DateOnly(2010, 1, 1), new DateOnly(2015, 12, 31), 3, 6, 6);

        var results = WalkForwardEngine.Run(windows, (start, end) =>
            SelectionFixture.MakeResult(start, end, new[] { SelectionFixture.MakeTrade("ABC", 100m) }));

        var first = results[0];
        Assert.Equal(first.Window.TrainStart, first.TrainResult.Run.StartDate);
        Assert.Equal(first.Window.TrainEnd, first.TrainResult.Run.EndDate);
        Assert.Equal(first.Window.ValidateStart, first.ValidateResult.Run.StartDate);
        Assert.Equal(first.Window.TestStart, first.TestResult.Run.StartDate);
    }

    [Fact]
    public void Run_EmptyWindowList_ReturnsEmpty_NeverCallsDelegate()
    {
        var called = false;
        var results = WalkForwardEngine.Run(Array.Empty<WalkForwardWindow>(), (s, e) => { called = true; return SelectionFixture.MakeResult(s, e, Array.Empty<Trade>()); });

        Assert.Equal(0, results.Count);
        Assert.False(called);
    }
}
