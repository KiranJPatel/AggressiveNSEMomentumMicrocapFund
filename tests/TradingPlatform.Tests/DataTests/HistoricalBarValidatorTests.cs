using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Validation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class HistoricalBarValidatorTests
{
    private static HistoricalBar MakeBar(decimal open, decimal high, decimal low, decimal close, long volume, DateTimeOffset? ts = null) => new()
    {
        Symbol = "TEST",
        Timestamp = ts ?? new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(5.5)),
        Open = open, High = high, Low = low, Close = close, AdjustedClose = close,
        Volume = volume, Source = "Yahoo", Timeframe = Timeframe.Daily
    };

    [Fact]
    public void ValidBar_ProducesNoIssues()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 105, 99, 103, 5000) });
        Assert.Equal(0, issues.Count);
    }

    [Fact]
    public void HighLessThanLow_FlaggedAsError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 95, 99, 97, 5000) });
        Assert.True(issues.Any(i => i.IssueType == "InvalidHighLow" && i.Severity == DataQualitySeverity.Error));
    }

    [Fact]
    public void HighBelowClose_FlaggedAsError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 102, 99, 110, 5000) });
        Assert.True(issues.Any(i => i.IssueType == "HighBelowOpenOrClose"));
    }

    [Fact]
    public void LowAboveOpen_FlaggedAsError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 110, 105, 108, 5000) });
        Assert.True(issues.Any(i => i.IssueType == "LowAboveOpenOrClose"));
    }

    [Fact]
    public void NegativePrice_FlaggedAsError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(-100, 105, 99, 103, 5000) });
        Assert.True(issues.Any(i => i.IssueType == "NonPositivePrice"));
    }

    [Fact]
    public void ZeroVolume_FlaggedAsInfoOnly_NotError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 105, 99, 103, 0) });
        var issue = issues.Single(i => i.IssueType == "ZeroVolume");
        Assert.Equal(DataQualitySeverity.Info, issue.Severity);
    }

    [Fact]
    public void NegativeVolume_FlaggedAsError()
    {
        var issues = HistoricalBarValidator.Validate(new[] { MakeBar(100, 105, 99, 103, -500) });
        Assert.True(issues.Any(i => i.IssueType == "NegativeVolume"));
    }

    [Fact]
    public void DuplicateTimestampInBatch_FlaggedAsWarning()
    {
        var ts = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(5.5));
        var issues = HistoricalBarValidator.Validate(new[]
        {
            MakeBar(100, 105, 99, 103, 5000, ts),
            MakeBar(101, 106, 100, 104, 6000, ts),
        });
        Assert.True(issues.Any(i => i.IssueType == "DuplicateTimestampInBatch"));
    }

    [Fact]
    public void ValidatorNeverThrows_AndNeverMutatesInput()
    {
        var bar = MakeBar(-1, -2, -3, -4, -5);
        var originalOpen = bar.Open;
        HistoricalBarValidator.Validate(new[] { bar });
        Assert.Equal(originalOpen, bar.Open); // still -1, unaltered — flag, never fix (spec §5.3)
    }
}
