using TradingPlatform.Core.Configuration;
using TradingPlatform.Scanner.Scheduling;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ScannerTests;

public class MarketHoursCalculatorTests
{
    private static readonly ScannerOptions Options = new()
    {
        MarketOpenLocalTime = "09:15",
        MarketCloseLocalTime = "15:30",
        MarketTimeZoneId = "Asia/Kolkata",
    };

    // 2026-03-02 is a Monday.
    private static DateTimeOffset MondayAt(int hour, int minute) =>
        new DateTimeOffset(2026, 3, 2, hour, minute, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();

    [Fact]
    public void DuringMarketHours_OnAWeekday_IsOpen()
    {
        Assert.True(MarketHoursCalculator.IsMarketOpen(MondayAt(11, 0), Options));
    }

    [Fact]
    public void BeforeMarketOpen_IsClosed()
    {
        Assert.False(MarketHoursCalculator.IsMarketOpen(MondayAt(8, 0), Options));
    }

    [Fact]
    public void AfterMarketClose_IsClosed()
    {
        Assert.False(MarketHoursCalculator.IsMarketOpen(MondayAt(16, 0), Options));
    }

    [Fact]
    public void ExactlyAtOpen_IsOpen()
    {
        Assert.True(MarketHoursCalculator.IsMarketOpen(MondayAt(9, 15), Options));
    }

    [Fact]
    public void ExactlyAtClose_IsOpen()
    {
        Assert.True(MarketHoursCalculator.IsMarketOpen(MondayAt(15, 30), Options));
    }

    [Fact]
    public void Saturday_IsClosed_EvenDuringMarketHours()
    {
        // 2026-03-07 is a Saturday.
        var saturday = new DateTimeOffset(2026, 3, 7, 11, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        Assert.False(MarketHoursCalculator.IsMarketOpen(saturday, Options));
    }

    [Fact]
    public void Sunday_IsClosed()
    {
        var sunday = new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        Assert.False(MarketHoursCalculator.IsMarketOpen(sunday, Options));
    }

    [Fact]
    public void TimeUntilNextOpen_WhenAlreadyOpen_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, MarketHoursCalculator.TimeUntilNextOpen(MondayAt(11, 0), Options));
    }

    [Fact]
    public void TimeUntilNextOpen_BeforeTodaysOpen_IsTodayAtOpenTime()
    {
        var wait = MarketHoursCalculator.TimeUntilNextOpen(MondayAt(8, 0), Options);
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(15), wait);
    }

    [Fact]
    public void TimeUntilNextOpen_AfterTodaysClose_IsTomorrowAtOpenTime()
    {
        var wait = MarketHoursCalculator.TimeUntilNextOpen(MondayAt(16, 0), Options);
        // Monday 16:00 -> Tuesday 09:15 = 17h15m
        Assert.Equal(TimeSpan.FromHours(17) + TimeSpan.FromMinutes(15), wait);
    }

    [Fact]
    public void TimeUntilNextOpen_OnFridayAfterClose_SkipsToMonday()
    {
        // 2026-03-06 is a Friday.
        var fridayAfterClose = new DateTimeOffset(2026, 3, 6, 16, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        var wait = MarketHoursCalculator.TimeUntilNextOpen(fridayAfterClose, Options);
        // Friday 16:00 -> Monday 09:15 — compute directly instead of hardcoding the raw span.
        var expectedOpen = new DateTimeOffset(2026, 3, 9, 9, 15, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(expectedOpen.ToUniversalTime() - fridayAfterClose, wait);
    }
}
