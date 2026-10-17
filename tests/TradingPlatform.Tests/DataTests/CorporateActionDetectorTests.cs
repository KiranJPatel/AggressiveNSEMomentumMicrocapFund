using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Validation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class CorporateActionDetectorTests
{
    private static HistoricalBar MakeBar(int dayOffset, decimal close, decimal adjClose) => new()
    {
        Symbol = "TEST",
        Timestamp = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = adjClose,
        Volume = 10000, Source = "Yahoo", Timeframe = Timeframe.Daily
    };

    [Fact]
    public void NormalDayToDayMove_NoIssuesRaised()
    {
        var bars = new[] { MakeBar(0, 100, 100), MakeBar(1, 102, 102) }; // 2% move
        var issues = CorporateActionDetector.Detect(bars, extremeGapPercentThreshold: 15m);
        Assert.Equal(0, issues.Count);
    }

    [Fact]
    public void SplitLikePattern_RawJumpsButAdjustedDoesNot_FlaggedAsLikelyCorporateAction()
    {
        // Classic 1:2 split: raw close halves, adjusted close stays continuous.
        var bars = new[] { MakeBar(0, 200, 200), MakeBar(1, 100, 200) };
        var issues = CorporateActionDetector.Detect(bars, extremeGapPercentThreshold: 15m);

        Assert.Equal(1, issues.Count);
        Assert.Equal("LikelyCorporateAction", issues[0].IssueType);
    }

    [Fact]
    public void ExtremeMoveMirroredInAdjustedClose_FlaggedAsUnexplainedGap_NotCorporateAction()
    {
        // Genuine ~30% crash reflected in both raw and adjusted close equally.
        var bars = new[] { MakeBar(0, 100, 100), MakeBar(1, 70, 70) };
        var issues = CorporateActionDetector.Detect(bars, extremeGapPercentThreshold: 15m);

        Assert.Equal(1, issues.Count);
        Assert.Equal("ExtremeUnexplainedGap", issues[0].IssueType);
    }

    [Fact]
    public void SingleBar_NoComparisonPossible_NoIssues()
    {
        var issues = CorporateActionDetector.Detect(new[] { MakeBar(0, 100, 100) }, extremeGapPercentThreshold: 15m);
        Assert.Equal(0, issues.Count);
    }

    [Fact]
    public void MoveBelowThreshold_NeverFlagged_RegardlessOfAdjustedClose()
    {
        var bars = new[] { MakeBar(0, 100, 100), MakeBar(1, 105, 100) }; // 5% raw move, threshold 15%
        var issues = CorporateActionDetector.Detect(bars, extremeGapPercentThreshold: 15m);
        Assert.Equal(0, issues.Count);
    }
}
