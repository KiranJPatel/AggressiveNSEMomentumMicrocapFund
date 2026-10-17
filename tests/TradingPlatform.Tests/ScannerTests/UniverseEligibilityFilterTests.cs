using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Scanner.Universe;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ScannerTests;

public class UniverseEligibilityFilterTests
{
    private static readonly ScannerOptions Options = new()
    {
        MinimumAverageDailyTurnover = 1_00_00_000m,
        MinimumPrice = 20m,
        MinimumHistoricalBarsRequired = 200,
    };

    private static Instrument ActiveInstrument(decimal? adtv = 2_00_00_000m) => new()
    {
        Symbol = "ABC", NseSymbol = "ABC", YahooSymbol = "ABC.NS", CompanyName = "ABC Ltd",
        Exchange = "NSE", Active = true, AverageDailyTurnover = adtv,
    };

    private static SurveillanceStatus ClearSurveillance() => new() { Symbol = "ABC", AsOfDate = new DateOnly(2026, 1, 5), Flags = RiskFlags.None, Availability = DataAvailability.Available };

    [Fact]
    public void AllChecksClean_IsEligible()
    {
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(), 100m, 250, ClearSurveillance(), Options);
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void InactiveInstrument_IsIneligible()
    {
        var instrument = ActiveInstrument();
        instrument.Active = false;
        var result = UniverseEligibilityFilter.Evaluate(instrument, 100m, 250, ClearSurveillance(), Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void PriceBelowMinimum_IsIneligible()
    {
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(), 10m, 250, ClearSurveillance(), Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void TurnoverBelowMinimum_IsIneligible()
    {
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(adtv: 1_000_000m), 100m, 250, ClearSurveillance(), Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void InsufficientHistoricalData_IsIneligible()
    {
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(), 100m, 50, ClearSurveillance(), Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void UnknownSurveillanceStatus_IsIneligible_NeverTreatedAsSafe()
    {
        var unknown = new SurveillanceStatus { Symbol = "ABC", AsOfDate = new DateOnly(2026, 1, 5), Flags = RiskFlags.None, Availability = DataAvailability.Unknown };
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(), 100m, 250, unknown, Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void SurveillanceFlagged_IsIneligible()
    {
        var flagged = new SurveillanceStatus { Symbol = "ABC", AsOfDate = new DateOnly(2026, 1, 5), Flags = RiskFlags.Asm, Availability = DataAvailability.Available };
        var result = UniverseEligibilityFilter.Evaluate(ActiveInstrument(), 100m, 250, flagged, Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void MissingYahooSymbol_IsIneligible()
    {
        var instrument = ActiveInstrument();
        instrument.YahooSymbol = "";
        var result = UniverseEligibilityFilter.Evaluate(instrument, 100m, 250, ClearSurveillance(), Options);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void AllChecksRun_EvenAfterAFailure()
    {
        var instrument = ActiveInstrument();
        instrument.Active = false;
        var result = UniverseEligibilityFilter.Evaluate(instrument, 5m, 10, ClearSurveillance(), Options);

        Assert.True(result.Checks.Any(c => c.Name == "Active" && !c.Passed));
        Assert.True(result.Checks.Any(c => c.Name == "MinimumPrice" && !c.Passed));
        Assert.True(result.Checks.Any(c => c.Name == "SufficientHistoricalData" && !c.Passed));
    }
}
