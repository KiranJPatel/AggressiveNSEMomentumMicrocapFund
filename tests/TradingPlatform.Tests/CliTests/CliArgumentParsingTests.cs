using TradingPlatform.Application.Cli;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// Covers a real bug found during the 2026-09-13 build-verification session
/// (see docs/gap-log.md's "Twenty-sixth item"): every date/integer CLI
/// argument parser in this project used the pattern
/// `if (args[i] == flag &amp;&amp; TryParse(args[i+1], out var v)) result = v;` — when
/// the user supplied a malformed value (e.g. `--from not-a-date`), the
/// condition was simply false and the flag was silently treated as absent,
/// falling back to a default date range/value with no warning at all. Fixed
/// to distinguish "flag not supplied" (use the default) from "flag supplied
/// but unparseable" (fail loudly, exit 1) in BacktestCommand, MonteCarloCommand,
/// WalkForwardCommand, and OptimizeCommand. These parsing helpers are
/// `internal` (not part of any public API) — visible here only via
/// InternalsVisibleTo (see src/TradingPlatform.Application/AssemblyInfo.cs).
/// </summary>
public class CliArgumentParsingTests
{
    // --- BacktestCommand.ParseDateRange ---

    [Fact]
    public void ParseDateRange_NoArgs_DefaultsToLastThreeYears()
    {
        var (from, to, error) = BacktestCommand.ParseDateRange(new[] { "backtest" });
        Assert.Null(error);
        Assert.Equal(to.AddYears(-3), from);
    }

    [Fact]
    public void ParseDateRange_ValidFromAndTo_UsesGivenValues()
    {
        var (from, to, error) = BacktestCommand.ParseDateRange(new[] { "backtest", "--from", "2024-01-15", "--to", "2024-06-30" });
        Assert.Null(error);
        Assert.Equal(new DateOnly(2024, 1, 15), from);
        Assert.Equal(new DateOnly(2024, 6, 30), to);
    }

    [Fact]
    public void ParseDateRange_MalformedFrom_ReturnsErrorInsteadOfSilentDefault()
    {
        var (_, _, error) = BacktestCommand.ParseDateRange(new[] { "backtest", "--from", "not-a-date", "--to", "2024-06-30" });
        Assert.NotNull(error);
        Assert.True(error!.Contains("--from"));
    }

    [Fact]
    public void ParseDateRange_MalformedTo_ReturnsErrorInsteadOfSilentDefault()
    {
        var (_, _, error) = BacktestCommand.ParseDateRange(new[] { "backtest", "--to", "31/02/2024xyz" });
        Assert.NotNull(error);
        Assert.True(error!.Contains("--to"));
    }

    [Fact]
    public void ParseDateRange_FromNotBeforeTo_ReturnsOrderingError()
    {
        var (_, _, error) = BacktestCommand.ParseDateRange(new[] { "backtest", "--from", "2024-06-30", "--to", "2024-01-15" });
        Assert.NotNull(error);
        Assert.True(error!.Contains("must be before"));
    }

    // --- MonteCarloCommand.ParseLongArg ---

    [Fact]
    public void ParseLongArg_FlagAbsent_ReturnsNullNotInvalid()
    {
        var value = MonteCarloCommand.ParseLongArg(new[] { "montecarlo" }, "--seed", out var invalid);
        Assert.Null(value);
        Assert.False(invalid);
    }

    [Fact]
    public void ParseLongArg_ValidValue_ReturnsIt()
    {
        var value = MonteCarloCommand.ParseLongArg(new[] { "montecarlo", "--iterations", "5000" }, "--iterations", out var invalid);
        Assert.Equal(5000L, value);
        Assert.False(invalid);
    }

    [Fact]
    public void ParseLongArg_MalformedValue_FlagsInvalidInsteadOfSilentDefault()
    {
        var value = MonteCarloCommand.ParseLongArg(new[] { "montecarlo", "--iterations", "five-thousand" }, "--iterations", out var invalid);
        Assert.Null(value);
        Assert.True(invalid);
    }

    // --- WalkForwardCommand.ParseDateArg / ParseLongArg ---

    [Fact]
    public void WalkForward_ParseDateArg_MalformedValue_FlagsInvalid()
    {
        var value = WalkForwardCommand.ParseDateArg(new[] { "walkforward", "--to", "tomorrow-ish" }, "--to", out var invalid);
        Assert.Null(value);
        Assert.True(invalid);
    }

    [Fact]
    public void WalkForward_ParseLongArg_MalformedValue_FlagsInvalid()
    {
        var value = WalkForwardCommand.ParseLongArg(new[] { "walkforward", "--train-years", "three" }, "--train-years", out var invalid);
        Assert.Null(value);
        Assert.True(invalid);
    }

    [Fact]
    public void WalkForward_ParseLongArg_ValidValue_ReturnsIt()
    {
        var value = WalkForwardCommand.ParseLongArg(new[] { "walkforward", "--train-years", "4" }, "--train-years", out var invalid);
        Assert.Equal(4L, value);
        Assert.False(invalid);
    }

    // --- OptimizeCommand.ParseDateArg ---

    [Fact]
    public void Optimize_ParseDateArg_MalformedValue_FlagsInvalid()
    {
        var value = OptimizeCommand.ParseDateArg(new[] { "optimize", "--from", "not-a-real-date" }, "--from", out var invalid);
        Assert.Null(value);
        Assert.True(invalid);
    }

    [Fact]
    public void Optimize_ParseDateArg_ValidValue_ReturnsIt()
    {
        var value = OptimizeCommand.ParseDateArg(new[] { "optimize", "--from", "2023-04-01" }, "--from", out var invalid);
        Assert.Equal(new DateOnly(2023, 4, 1), value);
        Assert.False(invalid);
    }
}
