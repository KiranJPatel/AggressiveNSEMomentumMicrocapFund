using TradingPlatform.Data.Yahoo;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.DataTests;

public class MissingRangeCalculatorTests
{
    [Fact]
    public void FullyCached_ReturnsNoMissingRanges()
    {
        var from = new DateOnly(2026, 1, 5);
        var to = new DateOnly(2026, 1, 9);
        var cached = new HashSet<DateOnly> { from, from.AddDays(1), from.AddDays(2), from.AddDays(3), to };

        var result = MissingRangeCalculator.Calculate(from, to, cached);

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void FullyMissing_SingleWeek_ReturnsOneContiguousRange()
    {
        var from = new DateOnly(2026, 1, 5);  // Monday
        var to = new DateOnly(2026, 1, 9);    // Friday

        var result = MissingRangeCalculator.Calculate(from, to, new HashSet<DateOnly>());

        Assert.Equal(1, result.Count);
        Assert.Equal(from, result[0].From);
        Assert.Equal(to, result[0].To);
    }

    [Fact]
    public void WeekendInMiddle_SplitsIntoTwoRanges_ExcludingWeekendDays()
    {
        var from = new DateOnly(2026, 1, 1);  // Thursday
        var to = new DateOnly(2026, 1, 9);    // Friday (Jan 3-4 is Sat-Sun)

        var result = MissingRangeCalculator.Calculate(from, to, new HashSet<DateOnly>());

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), result[0].From);
        Assert.Equal(new DateOnly(2026, 1, 2), result[0].To);
        Assert.Equal(new DateOnly(2026, 1, 5), result[1].From);
        Assert.Equal(new DateOnly(2026, 1, 9), result[1].To);
    }

    [Fact]
    public void PartiallyCached_ReturnsOnlyGaps()
    {
        var from = new DateOnly(2026, 1, 5);  // Mon
        var to = new DateOnly(2026, 1, 9);    // Fri
        var cached = new HashSet<DateOnly> { new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6) }; // Mon, Tue cached

        var result = MissingRangeCalculator.Calculate(from, to, cached);

        Assert.Equal(1, result.Count);
        Assert.Equal(new DateOnly(2026, 1, 7), result[0].From); // Wed
        Assert.Equal(new DateOnly(2026, 1, 9), result[0].To);   // Fri
    }

    [Fact]
    public void WeekendOnlyRange_ReturnsNoRanges()
    {
        var from = new DateOnly(2026, 1, 3); // Saturday
        var to = new DateOnly(2026, 1, 4);   // Sunday

        var result = MissingRangeCalculator.Calculate(from, to, new HashSet<DateOnly>());

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void SingleMissingWeekday_ReturnsSingleDayRange()
    {
        var date = new DateOnly(2026, 1, 6); // Tuesday
        var result = MissingRangeCalculator.Calculate(date, date, new HashSet<DateOnly>());

        Assert.Equal(1, result.Count);
        Assert.Equal(date, result[0].From);
        Assert.Equal(date, result[0].To);
    }

    [Fact]
    public void ToBeforeFrom_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MissingRangeCalculator.Calculate(new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 1), new HashSet<DateOnly>()));
    }
}
